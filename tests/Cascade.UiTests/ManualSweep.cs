using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace Cascade.UiTests;

/// <summary>
/// Exploratory rig: drives the real app on a large generated log with a generated filter set, using actual
/// mouse and keyboard, and writes findings plus screenshots. Gated on CASCADE_MANUAL=1.
/// </summary>
public class ManualSweep : IDisposable
{
    private static readonly string Out =
        Environment.GetEnvironmentVariable("CASCADE_MANUAL_OUT")
        ?? Path.Combine(Path.GetTempPath(), "cascade-manual");
    private static readonly string Filters = Path.Combine(Out, "fixture.cascade");
    private const string PresetName = "gateway only";

    private readonly List<string> _log = new();
    private readonly List<string> _bugs = new();
    private CascadeApp _app = null!;
    private int _shot;

    /// <summary>Asked for by hand: it takes the mouse and the keyboard for several minutes, and generates a
    /// few hundred megabytes the first time. Anywhere else it must do NOTHING AT ALL, tearing down
    /// included.</summary>
    private static bool Asked => Environment.GetEnvironmentVariable("CASCADE_MANUAL") == "1";

    public void Dispose()
    {
        if (!Asked) return;
        Directory.CreateDirectory(Out);
        File.WriteAllLines(Path.Combine(Out, "log.txt"), _log);
        File.WriteAllLines(Path.Combine(Out, "bugs.txt"), _bugs.Count == 0 ? new[] { "none" } : _bugs.ToArray());
        try { _app?.Dispose(); } catch { }
    }

    private void Say(string s) => _log.Add(s);

    /// <summary>
    /// Every wait in this rig, in one place, so the idling can be counted and so there is a single seam to
    /// replace a flat wait with a real one.
    /// </summary>

    private static void Type(string text) => Timed("typing", () => Keyboard.Type(text));

    private static void Type(VirtualKeyShort key) => Timed("typing", () => Keyboard.Type(key));

    private static void Click(Point at) => Timed("mouse click", () => Mouse.Click(at));

    private static void Click(MouseButton button) => Timed("mouse click", () => Mouse.Click(button));


    private static void Wait(int ms)
    {
        System.Threading.Thread.Sleep(ms);
        Interlocked.Add(ref _slept, ms);
    }

    /// <summary>
    /// Waits for something to be true rather than for the clock to run down, giving up after
    /// <paramref name="cap"/>. Polls hard on purpose - a few hundred UI Automation reads cost far less than
    /// the seconds a flat wait throws away, and the cap keeps whatever tolerance the flat wait had.
    /// </summary>
    private static bool WaitUntil(Func<bool> settled, int cap, int poll = 25)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (settled()) return true;
            if (clock.ElapsedMilliseconds >= cap) return false;
            Wait(poll);
        }
    }

    /// <summary>
    /// Waits for a reading to change from <paramref name="was"/> and then stop moving.
    ///
    /// <para>DELIBERATELY NOT TOLD WHAT TO WAIT FOR. Waiting until the answer is the expected one would
    /// make the check that follows assert something this method has already guaranteed, which is how a
    /// suite ends up green over a thing it stopped testing. This only ever waits for the app to finish
    /// answering; what the answer says is still the check's to judge.</para>
    ///
    /// <para>Falling back to the cap is correct rather than a failure: a reading that legitimately does not
    /// change leaves the check to say so, exactly as the flat wait it replaces did.</para>
    /// </summary>
    private static string WaitForChange(Func<string> read, string was, int cap, int poll = 25)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string now = read();
        while (now == was && clock.ElapsedMilliseconds < cap) { Wait(poll); now = read(); }

        // And then hold still. A find bar counts up while it sweeps, so the first thing it says after the
        // old value is rarely the thing it settles on.
        int steady = 0;
        while (steady < 3 && clock.ElapsedMilliseconds < cap)
        {
            Wait(poll);
            string next = read();
            steady = next == now ? steady + 1 : 0;
            now = next;
        }
        return now;
    }

    /// <summary>Waits for a reading to stop moving, for the cases where what it started as is not known.</summary>
    private static string WaitForStill(Func<string> read, int cap, int poll = 25)
        => WaitForChange(read, "\u0000 never equal to a reading \u0000", cap, poll);

    private static long _slept;

    // What the run spent its time on, by kind. A rig this slow is worth optimising only where the time
    // really is, and the first guess here was wrong once already.
    private static readonly Dictionary<string, (int Count, long Ms)> Spent = new(StringComparer.Ordinal);

    private static T Timed<T>(string what, Func<T> run)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { return run(); }
        finally
        {
            lock (Spent)
            {
                Spent.TryGetValue(what, out var was);
                Spent[what] = (was.Count + 1, was.Ms + clock.ElapsedMilliseconds);
            }
        }
    }

    private static void Timed(string what, Action run) => Timed<bool>(what, () => { run(); return true; });

    /// <summary>
    /// The size every stage starts from: maximised, unless CASCADE_MANUAL_SIZE names one.
    ///
    /// <para>Maximised means something different on every desktop, and a hosted runner's is 1024x768 - far
    /// less room than the screen this rig was written on. A stage that quietly assumes a paneful of rows
    /// is where that goes wrong first, and it goes wrong as "menu item not found" three stages later.
    /// Naming a size is how the runner's geometry is rehearsed before a nightly run is trusted; ask for
    /// device pixels, so on a 150% display 1536x1152 is the runner's 1024x768 worth of room.</para>
    /// </summary>
    private void FitWindow()
    {
        var parts = (Environment.GetEnvironmentVariable("CASCADE_MANUAL_SIZE") ?? "")
            .Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h))
        {
            _app.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
            return;
        }

        // Compared against what the last resize ACHIEVED, not against what was asked for: MainForm has a
        // minimum size, so a request under it settles higher, and ResizeTo refuses a resize that changes
        // nothing - which is every call after the first.
        if (_app.Window.BoundingRectangle.Size == _fitted)
        {
            _app.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
            return;
        }

        _fitted = _app.ResizeTo(w, h);
        Say($"window {_fitted.Width}x{_fitted.Height} (asked for {w}x{h})");
    }

    private Size _fitted;

    private void Check(string what, bool ok, string detail = "")
    {
        _log.Add($"{(ok ? "ok  " : "BAD ")} {what}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
        if (!ok) _bugs.Add($"{what} :: {detail}");
    }

    /// <summary>What went wrong and where it was said. A stage makes dozens of calls, so the message on its
    /// own leaves the reader guessing: a nightly reported "An event was unable to invoke any of the
    /// subscribers" and nothing said which UI Automation call had said it. The frames are filtered to this
    /// assembly, which is the only part of the stack anybody here can act on.</summary>
    private static string Describe(Exception ex)
    {
        var mine = (ex.StackTrace ?? "").Split('\n')
                     .Select(line => line.Trim())
                     .Where(line => line.Contains("Cascade.UiTests", StringComparison.Ordinal))
                     .Take(3);
        string where = string.Join(" <- ", mine);
        return $"{ex.GetType().Name}: {ex.Message}{(where.Length > 0 ? " " + where : "")}";
    }

    [Fact]
    public void Sweep()
    {
        if (!Asked) return;
        SetProcessDpiAwarenessContext(-4);
        Directory.CreateDirectory(Out);
        // Written fresh each run, so saving and editing can be exercised from a known state.
        BigFixture.WriteFilters(Filters);

        _app = CascadeApp.LaunchExisting(BigFixture.Log(), Filters, CascadeApp.NewSettingsDir(),
                                         ownsFiles: false, ownsSettingsDir: true);
        FitWindow();
        Wait(1500);
        _app.Activate();
        WaitIndexed();
        Say($"launched: {Status()}");

        string only = Environment.GetEnvironmentVariable("CASCADE_MANUAL_ONLY") ?? "";
        var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        void Stage(string name, Action run)
        {
            if (wanted.Length > 0 && name != "content" && !wanted.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long sleptBefore = Interlocked.Read(ref _slept);
            Say($"===== {name} =====");
            try { Timed("stage body", run); }
            catch (Exception ex) { Check($"{name} ran to the end", false, Describe(ex)); }
            finally
            {
                // Whatever a stage did to the window, the next one starts from the same place - and above
                // all with no modal dialog standing over it, since one of those makes every stage after it
                // quietly do nothing. A hover tip counts: it is a top-level window of its own, and while one
                // is up the main window reports no title, so the next stage finds nothing to drive.
                var settling = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    ReleaseKeys();
                    ParkPointer();
                    DismissDialogs();
                    FitWindow();
                    WaitForStill(() => _app.Window.BoundingRectangle.ToString(), 700, poll: 25);
                    Timed("activate", () => _app.Activate());
                    Wait(100);
                }
                catch { /* best effort */ }

                long slept = Interlocked.Read(ref _slept) - sleptBefore;
                Say($"----- {name}: {clock.ElapsedMilliseconds:N0} ms, {slept:N0} of it waiting, " +
                    $"{settling.ElapsedMilliseconds:N0} settling afterwards -----");
            }
        }

        Stage("content", GetContentOnScreen);
        Stage("tooltip", FilterTooltip);
        Stage("selection", CharacterSelection);
        Stage("wrap", WordWrap);
        Stage("map", MatchMap);
        Stage("find", FindEverything);
        Stage("highlight", FindHighlighting);
        Stage("undo", UndoRedo);
        Stage("presets", Presets);
        Stage("columns", ColumnsAndWrap);
        Stage("newfilter", FilterFromSelection);
        Stage("backwards", FindBackwardsAndRegex);
        Stage("wrapfind", WrapWithFindAndSelection);
        Stage("tipoff", TooltipCanBeTurnedOff);
        Stage("undomenu", UndoMenuWording);
        Stage("presetedit", PresetEditing);
        Stage("markers", MarkersAndMap);
        Stage("roundtrip", PresetRoundTrip);
        Stage("goto", GoToAndZoom);
        Stage("precedence", ExcludePrecedence);

        Say($"bugs found: {_bugs.Count}");
        Say($"total waiting: {Interlocked.Read(ref _slept):N0} ms");
        lock (Spent)
            foreach (var kind in Spent.OrderByDescending(k => k.Value.Ms))
                Say($"spent on {kind.Key}: {kind.Value.Ms:N0} ms over {kind.Value.Count:N0} calls");

        // It used to end `Assert.True(true)`: every finding went to bugs.txt and the run reported PASSED
        // whatever it had seen. So a rig that had quietly rotted looked exactly like a rig that had found
        // nothing, which is how this one once sat at 17 bad on a clean tree for weeks with nobody the
        // wiser. A check that cannot fail is not a check.
        Assert.True(_bugs.Count == 0,
                    $"{_bugs.Count} of {_bugs.Count + _log.Count(l => l.StartsWith("ok  ", StringComparison.Ordinal))} " +
                    "checks failed. This rig drives the real mouse and keyboard, so a click or a keypress " +
                    "during the run fails the stage it lands in AND every stage after it - re-run it on a " +
                    "desktop nobody is touching before believing any of this." +
                    Environment.NewLine + string.Join(Environment.NewLine, _bugs));
    }

    /// <summary>Markers draw down the map's left edge, so setting one has to change what it paints. Also on
    /// the scrollbar's trough, which is the only place a mark outside the map's window can appear.</summary>
    private void MarkersAndMap()
    {
        ClickRow(0.5);
        Wait(400);
        var map = MapElement();
        Check("the map is there to draw on", map is not null);
        if (map is null) return;

        int before = MapPixels(map, Color.FromArgb(200, 40, 40));
        Chord(VirtualKeyShort.KEY_1);
        WaitForChange(() => MapPixels(map, Color.FromArgb(200, 40, 40)).ToString(), before.ToString(), 1200, poll: 60);
        int after = MapPixels(map, Color.FromArgb(200, 40, 40));
        Say($"marker pixels on the map: {before} -> {after}");
        Check("setting a marker shows up on the map", after > before, $"{before} -> {after}");
        Shot("markers");

        // ...and walking to it must work.
        Menu("View", "Focus Text Area");
        Wait(300);
        Chord(VirtualKeyShort.HOME);
        Wait(600);
        string at = $"line {Caret()}";
        Type(VirtualKeyShort.KEY_1);
        Wait(900);
        Check("pressing 1 walks to the marked line", $"line {Caret()}" != at, $"{at} -> {$"line {Caret()}"}");

        Chord(VirtualKeyShort.KEY_1);   // clear it again
        Wait(800);
    }

    private int MapPixels(AutomationElement map, Color want)
    {
        var r = map.BoundingRectangle;
        if (r.Width <= 0 || r.Height <= 0) return 0;
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (Math.Abs(p.R - want.R) < 60 && Math.Abs(p.G - want.G) < 60 && Math.Abs(p.B - want.B) < 60) n++;
            }
        return n;
    }

    /// <summary>A preset has to survive being written to the filter file and read back.</summary>
    /// <summary>Going to a line by number, on a file where the numbers are in the tens of millions, and
    /// zooming - both of which a user reaches for constantly and neither of which anything else drives.</summary>
    private void GoToAndZoom()
    {
        Menu("View", "Focus Text Area");
        Wait(300);

        void OpenGoTo()
        {
            Keyboard.Pressing(VirtualKeyShort.CONTROL);
            Type(VirtualKeyShort.KEY_G);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Wait(900);
        }

        long Go(string typed)
        {
            OpenGoTo();
            var dlg = _app.FindDialog("Go To Line");
            if (dlg is null) return -1;
            var box = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
            if (box is null) { Type(VirtualKeyShort.ESCAPE); return -1; }
            box.Focus();
            Keyboard.Pressing(VirtualKeyShort.CONTROL);
            Type(VirtualKeyShort.KEY_A);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Type(typed);
            Wait(200);
            string wasCaret = CaretLine().ToString();
            Type(VirtualKeyShort.RETURN);
            WaitForChange(() => CaretLine().ToString(), wasCaret, 1500);
            return CaretLine();
        }

        // Deep into the file, but worked out from the fixture rather than named: the sweep used to run on a
        // 33-million-line trace and asked for line 20,000,000 of a file that now has four million.
        long deepWanted = BigFixture.Lines * 3L / 4;
        long deep = Go(deepWanted.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Say($"Go To {deepWanted:N0} landed on {deep}");
        // Only matching lines may be on show, in which case a number that is not one of them lands on the
        // nearest that is - so "near enough, and below" is the honest claim, not equality.
        Check($"Go To Line reaches a line {deepWanted:N0} down", deep >= deepWanted && deep < deepWanted + 1000,
              deep.ToString());
        // CaretLine only answers for a row that is on screen, so a real number IS the proof it got there.
        Check("and it is on screen, not merely selected", deep > 0,
              $"caret {deep}, top of view {FirstVisible()}");
        Shot("goto-deep");

        // A number past the end of the file must land at the end rather than nowhere.
        long past = Go((BigFixture.Lines * 10L).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Say($"Go To {BigFixture.Lines * 10L:N0} landed on {past}");
        Check("a line number past the end stops at the last line",
              past > BigFixture.Lines - 100 && past <= BigFixture.Lines, past.ToString());

        // With only matching lines on show, a hidden number cannot be gone to - it lands on the nearest
        // line that is shown, which for line 1 is whatever the top of the file has become.
        Menu("View", "Focus Text Area");
        Wait(300);
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        Type(VirtualKeyShort.HOME);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        Wait(1200);
        long top = CaretLine();
        long firstShown = Go("1");
        Say($"Go To 1 landed on {firstShown}; the first shown line is {top}");
        Check("a number below the first line on show lands on that line", firstShown == top,
              $"went to {firstShown}, first shown is {top}");

        // Zoom: the text changes size, the status says so, and it comes back.
        string zoom = _app.StatusText("Zoom:");
        Menu("View", "Zoom In");
        Wait(600);
        Menu("View", "Zoom In");
        Wait(600);
        string bigger = _app.StatusText("Zoom:");
        Say($"zoom {zoom} -> {bigger}");
        Check("zooming in says so in the status bar", bigger != zoom, $"{zoom} -> {bigger}");
        int rows = Rows().Length;
        Menu("View", "Reset Zoom");
        Wait(800);
        Check("and resetting puts it back", _app.StatusText("Zoom:") == zoom,
              $"{_app.StatusText("Zoom:")}, was {zoom}");
        Check("and fewer lines fitted while it was bigger", rows < Rows().Length,
              $"{rows} rows zoomed in, {Rows().Length} at normal size");
        Shot("zoom-reset");
    }

    /// <summary>The line the caret is on. It used to be read out of the status bar's "Ln: X / Total", and
    /// when that went the parsing stayed behind and answered -1 to everything.</summary>
    private long CaretLine() => Caret();

    private void PresetRoundTrip()
    {
        var names = SafePresetNames();
        if (names.Length == 0)
        {
            Menu("Filters", "Presets");
            Wait(400);
            Type(VirtualKeyShort.ESCAPE);
            Wait(300);
            // Make one from the pane instead.
            var hint = _app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                           .FirstOrDefault(t => (t.Name ?? "").StartsWith("No presets yet", StringComparison.Ordinal));
            if (hint is null) { Check("somewhere to make a preset", false); return; }
            var hr = hint.BoundingRectangle;
            Mouse.MoveTo(new Point(hr.Left + hr.Width / 2, hr.Top + 40));
            Click(MouseButton.Right);
            Wait(800);
            Type(VirtualKeyShort.DOWN);
            Wait(200);
            string wasNaming = PresetList();
            Type(VirtualKeyShort.RETURN);
            Wait(1200);
            Type("round trip");
            Wait(300);
            Type(VirtualKeyShort.RETURN);
            WaitForChange(PresetList, wasNaming, 1200);
        }
        names = SafePresetNames();
        Check("there is a preset to save", names.Length > 0, string.Join("|", names));
        if (names.Length == 0) return;

        Chord(VirtualKeyShort.KEY_S);
        // Watched on disk: the save is the only thing here that leaves the window entirely.
        string wasSaved = File.Exists(Filters) ? File.ReadAllText(Filters) : "";
        WaitForChange(() => File.Exists(Filters) ? File.ReadAllText(Filters) : "", wasSaved, 2500, poll: 50);
        string saved = File.ReadAllText(Filters);
        Say($"filter file now {saved.Length} bytes, presets section: {saved.Contains("presets")}");
        Check("the preset is written to the filter file", saved.Contains("presets") && saved.Contains(names[0].Split(' ')[0]),
              $"{saved.Length} bytes");
        Check("and the title is no longer dirty", !(_app.Window.Title ?? "").Contains('*'), _app.Window.Title ?? "");
        Shot("roundtrip");
    }

    /// <summary>Wrapping changes where every character is, so the marks and the hit test have to follow.</summary>
    private void WrapWithFindAndSelection()
    {
        Narrow(1000, 820);

        CtrlF();
        if (_app.FindBar() is null) { Check("the bar opened", false, DescribePanes()); return; }
        var edit = _app.FindInput();
        _app.SetText(edit, "");
        Wait(300);
        Type(BigFixture.EveryLineTerm);   // typed, as a user would
        WaitForStill(() => MarkedPixels().ToString(), 1500, poll: 60);
        int typed = MarkedPixels();
        Say($"marks while the bar is open: {typed}");

        // The bar stays open throughout: Esc would close it and drop the marks with it, which is the one
        // thing that would make "do the marks survive wrapping" unanswerable.
        Check("the marks are there before wrapping", typed > 200, $"{typed} marked pixels");
        Menu("View", "Word Wrap");
        WaitForStill(() => MarkedPixels().ToString(), 1800, poll: 60);
        int wrapped = MarkedPixels();
        Say($"marked pixels flat {typed} -> wrapped {wrapped}");
        Check("the marks survive wrapping", wrapped > 200, $"{typed} -> {wrapped}");
        Shot("wrap-with-marks");

        // Select text on a wrapped row's SECOND segment: the hit test has to know which segment it is on.
        var tall = Rows().FirstOrDefault(x => x.BoundingRectangle.Height > 60);
        if (tall is not null)
        {
            var tr = tall.BoundingRectangle;
            int y = tr.Top + tr.Height - 10;
            Mouse.MoveTo(new Point(tr.Left + 200, y));
            Mouse.Down(MouseButton.Left);
            Mouse.MoveTo(new Point(tr.Left + 330, y));
            Wait(150);
            Mouse.Up(MouseButton.Left);
            Wait(500);
            string picked = CopyToClipboard();
            Say($"selected on a wrapped segment: '{Trim(picked)}'");
            Check("text can be picked out of a wrapped segment", picked.Length > 0 && !picked.Contains('\n'),
                  $"'{Trim(picked)}'");
            string whole = tall.Patterns.LegacyIAccessible.Pattern.Name.ValueOrDefault ?? "";
            Check("and it comes from the later part of the line",
                  picked.Length > 0 && whole.Contains(picked) && whole.IndexOf(picked, StringComparison.Ordinal) > 40,
                  $"'{Trim(picked)}' at {whole.IndexOf(picked, StringComparison.Ordinal)}");
            Shot("wrap-selection");
        }

        Menu("View", "Word Wrap");
        WaitForStill(RowShape, 1200, poll: 40);
        Menu("View", "Focus Text Area");
        Wait(300);
        Type(VirtualKeyShort.ESCAPE);
        Wait(600);
    }

    /// <summary>Takes the window down to a size where the lines really do have to wrap.</summary>
    private void Narrow(int w, int h)
    {
        _app.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        Wait(700);
        var t = _app.Window.Patterns.Transform.PatternOrDefault;
        t?.Move(80, 60);
        t?.Resize(w, h);
        Wait(600);
        _app.Activate();
        Wait(800);
        Say($"narrowed to {_app.Window.BoundingRectangle.Width}x{_app.Window.BoundingRectangle.Height}");
    }

    private void TooltipCanBeTurnedOff()
    {
        Menu("View", "Show Matching Filters on Hover");
        Wait(500);
        var rows = Rows();
        if (rows.Length < 4) { Check("rows to hover", false); return; }
        var r = rows[3].BoundingRectangle;
        Mouse.MoveTo(new Point(r.Left + 400, r.Top + r.Height / 2));
        Wait(1600);
        Check("turned off, hovering says nothing", TooltipWindow() is null, DescribeTopLevel());

        Menu("View", "Show Matching Filters on Hover");
        Wait(500);
        Mouse.MoveTo(new Point(r.Left + 200, r.Top + r.Height / 2));
        Wait(1600);
        Check("and turned back on it speaks again", TooltipWindow() is not null, DescribeTopLevel());
        Mouse.MoveTo(new Point(r.Left + 200, r.Top - 250));
        Wait(600);
    }

    private void UndoMenuWording()
    {
        var node = _app.FilterNode(BigFixture.MidFilter) ?? _app.FilterNode(BigFixture.HugeFilter);
        if (node is null) { Check("a filter to edit", false); return; }
        if (!ClickFilterRow(BigFixture.MidFilter) && !ClickFilterRow(BigFixture.HugeFilter)) { Check("the filter is reachable", false); return; }
        Wait(500);
        string wasBeforeDup = Roots();
        Chord(VirtualKeyShort.KEY_D);
        WaitForChange(Roots, wasBeforeDup, 2000);

        var edit = OpenMenu("Edit");
        string undo = edit?.FirstOrDefault(m => (m.Name ?? "").StartsWith("Undo", StringComparison.Ordinal))?.Name ?? "";
        Say($"Edit menu undo item: '{undo}'");
        Check("the undo item names what it will take back", undo.Length > "Undo".Length, undo);
        Type(VirtualKeyShort.ESCAPE);
        Wait(400);
        Type(VirtualKeyShort.ESCAPE);
        Wait(400);

        if (!ClickFilterRow(BigFixture.MidFilter)) ClickFilterRow(BigFixture.HugeFilter);
        Wait(400);
        string wasBeforeUndo = Roots();
        Chord(VirtualKeyShort.KEY_Z);
        WaitForChange(Roots, wasBeforeUndo, 2000);
    }

    /// <summary>Puts a filter row on screen and clicks it, so the list has focus and that row is selected.
    /// Scrolled out of sight, a row's rectangle is empty and a click on it lands on the menu bar - and the
    /// list is deliberately left where the user put it, so it will not scroll itself back.</summary>
    private bool ClickFilterRow(string contains)
    {
        var node = _app.FilterNode(contains);
        if (node is null) return false;
        try { node.AsTreeItem().Select(); } catch { /* selecting is only to scroll it into view */ }
        Wait(400);
        var nr = (_app.FilterNode(contains) ?? node).BoundingRectangle;
        if (nr.Width <= 0 || nr.Height <= 0) { Say($"  ({contains} is still not on screen)"); return false; }
        Click(new Point(nr.Left + 40, nr.Top + nr.Height / 2));
        Wait(400);
        return true;
    }

    private void PresetEditing()
    {
        var names = SafePresetNames();
        if (names.Length == 0) { Check("a preset to edit", false); return; }
        var list = _app.PresetList();
        var item = list.FindAllChildren().FirstOrDefault();
        if (item is null) { Check("a preset item", false); return; }

        // Starting from a preset that is NOT in effect, or "clicking its name did not put it in effect"
        // cannot tell a working selection from one that applied it.
        if (_app.ActivePresets().Any(n => n.StartsWith(names[0].Split(' ')[0], StringComparison.Ordinal)))
        {
            string wasActive = string.Join("|", _app.ActivePresets());
            _app.UntickPreset(names[0]);
            WaitForChange(() => string.Join("|", _app.ActivePresets()), wasActive, 3000, poll: 50);
        }
        Check("the preset starts out of effect",
              !_app.ActivePresets().Any(n => n.StartsWith(names[0].Split(' ')[0], StringComparison.Ordinal)),
              _app.DescribePresets());

        // Past the leading square: a press there is the tick box, and would switch the preset's filters on.
        var r = item.BoundingRectangle;
        var onLabel = new Point(r.Left + r.Height + 30, r.Top + r.Height / 2);
        Click(onLabel);
        Wait(500);
        Check("clicking a preset's name does not put it in effect",
              !_app.ActivePresets().Any(n => n.StartsWith(names[0].Split(' ')[0], StringComparison.Ordinal)),
              _app.DescribePresets());

        // F2 renames.
        Type(VirtualKeyShort.F2);
        Wait(1200);
        ShotScreen("preset-rename");
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        Type(VirtualKeyShort.KEY_A);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        Type("renamed one");
        Wait(300);
        string beforeRename = PresetList();
        Type(VirtualKeyShort.RETURN);
        WaitForChange(PresetList, beforeRename, 1200);
        Say($"after rename: {string.Join(" | ", SafePresetNames())}");
        Check("F2 renames a preset", SafePresetNames().Any(n => n.Contains("renamed one")),
              string.Join("|", SafePresetNames()));

        // Delete removes it.
        Click(onLabel);
        Wait(400);
        string beforeDelete = PresetList();
        Type(VirtualKeyShort.DELETE);
        WaitForChange(PresetList, beforeDelete, 1200);
        Say($"after delete: {string.Join(" | ", SafePresetNames())}");
        Check("Delete removes it", !SafePresetNames().Any(n => n.Contains("renamed one")),
              string.Join("|", SafePresetNames()));
        Shot("preset-edited");
    }

    /// <summary>Ctrl+N has to carry the selected part of the line, not the whole of it.</summary>
    private void FilterFromSelection()
    {
        var rows = Rows();
        if (rows.Length < 6) { Check("rows to select in", false); return; }
        var r = rows[4].BoundingRectangle;
        int y = r.Top + r.Height / 2;

        // Dragged, not double-clicked: a double-click opens the editor by itself, which would make Ctrl+N
        // look as though it had worked whatever it did.
        Mouse.MoveTo(new Point(r.Left + 300, y));
        Mouse.Down(MouseButton.Left);
        Mouse.MoveTo(new Point(r.Left + 420, y));
        Wait(150);
        Mouse.Up(MouseButton.Left);
        Wait(500);
        string picked = CopyToClipboard();
        Say($"selected text for the filter: '{Trim(picked)}'");
        Check("there is a selection to carry", picked.Length > 0, $"'{Trim(picked)}'");

        Chord(VirtualKeyShort.KEY_N);
        Wait(1800);
        ShotScreen("newfilter");
        Say($"after Ctrl+N: {DescribeTopLevel()}");
        var box = FilterTextBox();
        Check("Ctrl+N opens the filter editor", box is not null, DescribeTopLevel());
        if (box is not null)
        {
            string prefilled = _app.TextOf(box);
            Say($"prefilled with: '{Trim(prefilled)}'");
            Check("prefilled with the selection, not the whole line", prefilled == picked.Trim(),
                  $"'{Trim(prefilled)}' vs '{Trim(picked)}'");
        }
        DismissDialogs();
    }

    private void FindBackwardsAndRegex()
    {
        // Every line on show for this stage: the sparse term and the regex both live on payment lines, and
        // the enabled filter shows gateway ones - so in filtered mode find correctly refuses to move, and
        // the stage would be measuring that instead of what it came to measure.
        Menu("View", "Show Only Filtered Lines");
        WaitFiltered();
        Menu("View", "Focus Text Area");
        Wait(300);
        CtrlF();
        if (_app.FindBar() is null) { Check("the bar opened", false, DescribePanes()); return; }
        var edit = _app.FindInput();

        _app.SetText(edit, BigFixture.SparseTerm);
        Wait(300);
        string beforeFirst = $"line {Caret()}";
        Type(VirtualKeyShort.RETURN);
        WaitForChange(() => $"line {Caret()}|{Tally()}", $"{beforeFirst}|{Tally()}", 3000);
        string first = $"line {Caret()}";
        Type(VirtualKeyShort.RETURN);
        WaitForChange(() => $"line {Caret()}", first, 1500);
        string second = $"line {Caret()}";
        Check("Enter goes forwards", second != first, $"{first} -> {second}");

        Keyboard.Pressing(VirtualKeyShort.SHIFT);
        Type(VirtualKeyShort.RETURN);
        Keyboard.Release(VirtualKeyShort.SHIFT);
        WaitForChange(() => $"line {Caret()}", second, 1500);
        Check("Shift+Enter goes back", $"line {Caret()}" == first,
              $"{second} -> {$"line {Caret()}"} (wanted {first})");

        var regex = _app.FindBar()?.FindFirstDescendant(cf => cf.ByName("Regex"))?.AsCheckBox();
        Check("there is a regex option", regex is not null);
        if (regex is not null)
        {
            regex.IsChecked = true;
            _app.SetText(edit, BigFixture.RegexTerm);
            Wait(400);
            string beforeRegex = Tally();
            Type(VirtualKeyShort.RETURN);
            WaitForChange(Tally, beforeRegex, 4000);
            Say($"regex search: {Tally()}");
            Check("a regex search finds something", Tally().StartsWith("Match ", StringComparison.Ordinal), Tally());

            // ...and one that cannot match must say so, or the regex is not really being used.
            _app.SetText(edit, BigFixture.ImpossibleRegexTerm);
            Wait(400);
            string beforeImpossible = Tally();
            Type(VirtualKeyShort.RETURN);
            WaitForChange(Tally, beforeImpossible, 6000);
            Say($"impossible regex: {Tally()}");
            Check("and one that cannot match says so", Tally() == "No matches", Tally());
            regex.IsChecked = false;
        }
        Shot("backwards");
        Type(VirtualKeyShort.ESCAPE);
        Wait(600);
        Menu("View", "Show Only Filtered Lines");   // back as the stage found it
        WaitFiltered();
        Menu("View", "Focus Text Area");
        Wait(300);
    }

    /// <summary>The marks themselves, counted off the screen - nothing else can tell whether the term is
    /// actually shown as marked.</summary>
    private void FindHighlighting()
    {
        Menu("View", "Focus Text Area");
        Wait(300);
        int plain = MarkedPixels();
        Check("nothing is marked to begin with", plain < 200, $"{plain} marked pixels");

        CtrlF();
        if (_app.FindBar() is null) { Check("the bar opened", false, DescribePanes()); return; }
        var edit = _app.FindInput();
        _app.SetText(edit, "");
        Wait(200);
        Type(BigFixture.EveryLineTerm);
        WaitForStill(() => MarkedPixels().ToString(), 1500, poll: 60);
        int typed = MarkedPixels();
        Say($"marked pixels: {plain} -> {typed} on typing alone");
        Check("typing alone marks what is on screen", typed > plain + 500, $"{plain} -> {typed}");
        Shot("highlight-typed");

        string beforeRun = Tally();
        Type(VirtualKeyShort.RETURN);
        WaitForChange(Tally, beforeRun, 2500);
        int found = MarkedPixels(current: true);
        Check("the line the search landed on is marked more strongly", found > 30, $"{found} strong pixels");
        Shot("highlight-found");

        // The map has to let go of the marks on the same keypress that drops the term. It decides whether
        // it has anything to redraw by comparing the hit count it last drew against the document's, so
        // being repainted before the sweep was released left it holding them until something else happened
        // to invalidate the view - a click, a scroll, anything. Nothing is touched between these two grabs.
        var map = MapElement();
        using var withHits = map is null ? null : Grab(map);
        Type(VirtualKeyShort.ESCAPE);   // closes the bar and drops the term in one gesture
        Wait(800);
        int cleared = MarkedPixels();
        Check("Esc takes the marks away with the bar", cleared < 200, $"{cleared} marked pixels");

        if (map is not null && withHits is not null)
        {
            using var afterEsc = Grab(map);
            double moved = PictureDiff(withHits, afterEsc);
            Say($"minimap on dropping the term: {moved:P1} of its pixels changed");
            Check("and the minimap lets go of them on the same keypress", moved > 0.01,
                  $"{moved:P1} of the map's pixels changed");
        }
        Shot("highlight-cleared");
    }

    private void ColumnsAndWrap()
    {
        // Word wrap and columns cannot both be on; the menu has to say so rather than quietly ignore it.
        var view = OpenMenu("View");
        var wrap = view?.FirstOrDefault(m => (m.Name ?? "") == "Word Wrap");
        Check("Word Wrap is offered", wrap is not null, string.Join("|", view?.Select(m => m.Name) ?? Array.Empty<string>()));
        Check("and is available while there are no columns", wrap?.IsEnabled ?? false);
        Type(VirtualKeyShort.ESCAPE);
        Wait(400);
        Type(VirtualKeyShort.ESCAPE);
        Wait(400);
    }

    private AutomationElement[]? OpenMenu(string name)
    {
        var bar = _app.Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar));
        var top = bar?.FindAllChildren(cf => cf.ByControlType(ControlType.MenuItem))
                     .FirstOrDefault(m => (m.Name ?? "") == name);
        if (top is null) return null;
        var r = top.BoundingRectangle;
        Click(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
        Wait(700);
        return _app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem));
    }

    /// <summary>Pixels in the log area painted in the find colours, straight off the screen.</summary>
    private int MarkedPixels(bool current = false)
    {
        var want = current ? Color.FromArgb(255, 170, 60) : Color.FromArgb(255, 236, 150);
        var r = _app.Grid().BoundingRectangle;
        using var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
        int n = 0;
        for (int y = 0; y < bmp.Height; y += 2)
            for (int x = 0; x < bmp.Width; x += 2)
            {
                var p = bmp.GetPixel(x, y);
                if (Math.Abs(p.R - want.R) < 12 && Math.Abs(p.G - want.G) < 12 && Math.Abs(p.B - want.B) < 12) n++;
            }
        return n;
    }

    // ---- stages ----

    /// <summary>Nothing in the saved set is enabled, so first make the view show something.</summary>
    private void GetContentOnScreen()
    {
        Check("the file is indexed", Status().Contains(BigFixture.TotalStatus), Status());

        var node = _app.FilterNode(BigFixture.HugeFilter);
        Check($"the {BigFixture.HugeFilter} filter is in the list", node is not null);
        if (node is null) return;

        node.AsTreeItem().Select();
        Wait(300);
        _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);   // the subtree, so plenty matches
        WaitFiltered();
        Say($"after enabling {BigFixture.HugeFilter}: {Status()}");
        Check("enabling it fills the view", Rows().Length > 0, $"{Rows().Length} rows");
        Check("and the count is no longer zero", !Status().Contains("Fil: 0"), Status());
        Shot("content");
    }

    private void FilterTooltip()
    {
        var rows = Rows();
        if (rows.Length < 4) { Check("there are rows to hover", false); return; }

        var r = rows[3].BoundingRectangle;
        Mouse.MoveTo(new Point(r.Left + 250, r.Top + r.Height / 2));
        Wait(1500);
        var tip = TooltipWindow();
        Check("hovering a line raises a tip", tip is not null, DescribeTopLevel());
        if (tip is not null)
        {
            string text = tip.Name ?? "";
            Say($"tip: {text.Replace("\n", " | ")}");
            Check("the tip names a filter that matched", text.Contains("api-gateway", StringComparison.OrdinalIgnoreCase), text);
        }
        ShotScreen("tooltip");

        Mouse.MoveTo(new Point(r.Left + 250, r.Top - 250));
        Wait(1000);
        Check("moving off the line takes the tip away", TooltipWindow() is null, DescribeTopLevel());
    }

    private void CharacterSelection()
    {
        var rows = Rows();
        if (rows.Length < 6) { Check("there are rows to select in", false); return; }
        var r = rows[4].BoundingRectangle;
        int y = r.Top + r.Height / 2;

        Mouse.MoveTo(new Point(r.Left + 200, y));
        Mouse.Down(MouseButton.Left);
        Mouse.MoveTo(new Point(r.Left + 330, y));
        Wait(150);
        Mouse.Up(MouseButton.Left);
        Wait(500);
        Shot("selection-drag");

        string dragged = CopyToClipboard();
        Say($"dragged copy: '{Trim(dragged)}'");
        Check("dragging inside a line copies just that text",
              dragged.Length > 0 && !dragged.Contains('\n'), $"'{Trim(dragged)}'");

        // A double-click in the log does NOT select a word: that gesture was deliberately given to writing
        // a filter for the line, carrying whatever was picked out. Leaving the dialog it opens standing is
        // what used to wreck every stage after this one, so it is put away before anything else happens.
        Mouse.MoveTo(new Point(r.Left + 260, y));
        Wait(400);
        Mouse.DoubleClick(MouseButton.Left);
        Wait(1500);
        ShotScreen("selection-double");
        var carried = FilterTextBox();
        Say($"double-click opened the editor with: '{Trim(carried is null ? "" : _app.TextOf(carried))}'");
        Check("double-clicking a line offers to make a filter from it", carried is not null, DescribeTopLevel());
        if (carried is not null)
            Check("carrying the text that was picked out", _app.TextOf(carried) == dragged.Trim(),
                  $"'{Trim(_app.TextOf(carried))}' vs '{Trim(dragged)}'");
        DismissDialogs();

        // A plain click takes the whole line, which is the only way to select one now.
        Click(new Point(r.Left + 260, y));
        Wait(600);
        Shot("selection-line");
        string line = CopyToClipboard();
        Say($"click copy: {line.Length} chars '{Trim(line)}'");
        Check("a plain click takes the whole line", line.Length > dragged.Length,
              $"{line.Length} vs {dragged.Length}");
    }

    private void WordWrap()
    {
        // Maximised, most lines fit, so nothing would wrap. Narrow it right down first.
        Narrow(900, 800);

        var before = Rows();
        int beforeHeight = before.Length > 0 ? before[0].BoundingRectangle.Height : 0;
        int beforeCount = before.Length;
        bool hadHBar = _app.HasHorizontalScrollBar();
        Shot("wrap-off");

        Menu("View", "Word Wrap");
        WaitForStill(RowShape, 1500, poll: 40);
        var after = Rows();
        int tallest = after.Length > 0 ? after.Max(x => x.BoundingRectangle.Height) : 0;
        Say($"rows {beforeCount}@{beforeHeight}px -> {after.Length}, tallest {tallest}px, hbar {hadHBar} -> {_app.HasHorizontalScrollBar()}");
        Check("wrapping makes long lines taller", tallest > beforeHeight, $"{beforeHeight} -> {tallest}");
        Check("fewer lines fit", after.Length < beforeCount, $"{beforeCount} -> {after.Length}");
        Check("the sideways scrollbar goes", !_app.HasHorizontalScrollBar());
        Check("the rows still run down the window in order",
              after.Zip(after.Skip(1)).All(p => p.Second.BoundingRectangle.Top >= p.First.BoundingRectangle.Bottom - 2),
              string.Join(",", after.Select(x => x.BoundingRectangle.Top)));
        Shot("wrap-on");

        Menu("View", "Focus Text Area");
        Wait(300);
        string at = $"line {Caret()}";
        for (int i = 0; i < 3; i++) { Type(VirtualKeyShort.DOWN); Wait(150); }
        Check("the caret still moves while wrapped", $"line {Caret()}" != at, $"{at} -> {$"line {Caret()}"}");

        // Clicking the lower half of a wrapped row must land on that row, not the one below.
        var rows = Rows();
        var tall = rows.FirstOrDefault(x => x.BoundingRectangle.Height > beforeHeight * 1.5);
        if (tall is not null)
        {
            string want = tall.Patterns.LegacyIAccessible.Pattern.Value.ValueOrDefault;
            var tr = tall.BoundingRectangle;
            Click(new Point(tr.Left + 200, tr.Bottom - 6));
            Wait(500);
            Check("clicking low in a wrapped line selects that line",
                  Caret().ToString(System.Globalization.CultureInfo.InvariantCulture) == want,
                  $"wanted {want}, got line {Caret()}");
        }

        Menu("View", "Word Wrap");
        WaitForStill(RowShape, 1200, poll: 40);
        Check("turning it off puts the rows back",
              Rows() is { Length: > 0 } back && back[0].BoundingRectangle.Height == beforeHeight,
              $"{(Rows() is { Length: > 0 } b2 ? b2[0].BoundingRectangle.Height : -1)} vs {beforeHeight}");
        Check("and brings the sideways scrollbar back", _app.HasHorizontalScrollBar() == hadHBar);
    }

    private void MatchMap()
    {
        Check("there is a scrollbar", _app.VerticalScrollerName().Length > 0, _app.VerticalScrollerName());
        Say($"scrollbar scale: {_app.ScrollBarScale()}");
        long first = FirstVisible();
        bool scrolled = _app.ScrollVerticalTo(Row(0.15));
        Check("and it scrolls the view", scrolled && FirstVisible() != first,
              $"{first} -> {FirstVisible()}");
        Shot("map");

        var map = MapElement();
        Check("the minimap is beside it, not instead of it", map is not null);
        if (map is not null)
        {
            // Nothing is coloured on the map that a filter is not colouring in the text. Switching them all
            // off is the sharpest form of that: the map has to go blank.
            _app.ScrollVerticalTo(150_000);
            WaitForStill(() => Colours(map), 900, poll: 60);
            var coloured = MapColours(map);
            Say($"colours with the filters on: {string.Join(" ", coloured)}");
            Check("the map is coloured while filters are on", coloured.Count > 0, string.Join(" ", coloured));

            string was = Colours(map);
            Menu("Filters", "Disable All");
            WaitFiltered();
            WaitForChange(() => Colours(map), was, 1500, poll: 60);
            var bare = MapColours(map);
            Say($"colours with every filter off: {string.Join(" ", bare)}");
            Check("and blank once none of them are", bare.Count == 0, string.Join(" ", bare));
            Shot("map-blank");

            if (ClickFilterRow(BigFixture.HugeFilter))
            {
                string blank = Colours(map);
                _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
                WaitFiltered();
                _app.ScrollVerticalTo(150_000);
                WaitForChange(() => Colours(map), blank, 1500, poll: 60);
                Check("and coloured again when one is turned back on", MapColours(map).Count > 0,
                      string.Join(" ", MapColours(map)));
                Shot("map-two-filters");
            }

            // The colours have to be the filters' own, on a real window and not just in a fixture.
            foreach (string name in new[] { BigFixture.HugeFilter, BigFixture.BusyFilter })
            {
                var own = FilterRowColour(name);
                if (own is null) { Say($"  (no colour on {name})"); continue; }
                Check($"the colour {name} paints its rows is on the map",
                      MapHasExactly(map, own.Value), $"row colour #{own.Value.R:x2}{own.Value.G:x2}{own.Value.B:x2}");
            }
            Shot("map-colours");

            // The rectangle that says where you are stays put, and the map under it moves: that is what
            // keeps the same amount of file on either side of you no matter where you scroll to. Both of
            // these rows are well inside the filtered view - past its end the map anchors to the bottom
            // instead, which is a different thing being tested below.
            // Every line on show for this one: with only matching lines shown and a single filter on, every
            // row in the map is that filter's colour, so the map is a solid block and two places in the file
            // are identical by construction - the check could only ever fail.
            // A SECOND filter as well, for the same reason: a pixel of the map stands for many rows now, so
            // one filter matching a third of the file colours every pixel of it and two places in the file
            // are again identical whatever the map is doing. Two filters give it a pattern to vary.
            if (ClickFilterRow(BigFixture.BusyFilter))
            {
                _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
                WaitFiltered();
            }
            Menu("View", "Show Only Filtered Lines");
            WaitFiltered();
            string wasHigh = FirstVisible().ToString();
            _app.ScrollVerticalTo(Row(0.15));
            WaitForChange(() => FirstVisible().ToString(), wasHigh, 900);
            long highLine = FirstVisible();
            using var atHigh = Grab(map);
            _app.ScrollVerticalTo(Row(0.85));
            WaitForChange(() => FirstVisible().ToString(), highLine.ToString(), 900);
            long lowLine = FirstVisible();
            using var atLow = Grab(map);
            Say($"map across a jump from 15% to 85% of the file ({highLine} -> {lowLine}): " +
                $"{PictureDiff(atHigh, atLow):P0} of the pixels changed");
            Check("the map shows somewhere else entirely after a long scroll", PictureDiff(atHigh, atLow) > 0.10,
                  $"{PictureDiff(atHigh, atLow):P0} of the pixels changed, view {highLine} -> {lowLine}");
            Menu("View", "Show Only Filtered Lines");
            WaitFiltered();
            // Put the second filter back off: the stages after this one share the window.
            if (ClickFilterRow(BigFixture.BusyFilter))
            {
                _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
                WaitFiltered();
            }

            // Clicking the map moves the view without the scrollbar going anywhere much: it is the fine
            // adjustment, and the file is far too long for a window of it to register on the whole scale.
            // FLAT WAITS ON PURPOSE, both of them. The map re-centres its window when the view moves on its
            // own, and that goes on for a while after the view itself has stopped - so "the reading changed
            // and held still for a moment" is NOT settled here, and a drag begun too early starts from
            // somewhere the map is still moving away from. MEASURED: waiting for the reading instead let the
            // drag start at 1,988,222 rather than the 1,984,382 the click left, and that was enough to stop
            // the drag check catching the one real fault this rig has - it reported the minimap answering a
            // drag at a small window size, which it does not. Worth 2.4 seconds of the run.
            _app.ScrollVerticalTo(Row(0.5));
            Wait(1200);
            var r = map.BoundingRectangle;
            long viewBefore = FirstVisible();
            Click(new Point(r.Left + r.Width / 2, r.Top + r.Height / 6));
            Wait(1200);
            Say($"clicking high on the map: {viewBefore} -> {FirstVisible()}");
            Check("clicking the map moves the view", FirstVisible() != viewBefore,
                  $"{viewBefore} -> {FirstVisible()}");
            Shot("map-viewport");

            DragIsLive("the minimap", map, r.Left + r.Width / 2, r.Top + r.Height / 4, r.Top + r.Height * 3 / 4);
            var bar = ScrollBarElement();
            if (bar is not null)
            {
                // Halfway down the view, so the thumb is halfway down the trough and the press lands on it
                // rather than paging.
                var br = bar.BoundingRectangle;
                string wasBar = FirstVisible().ToString();
                _app.ScrollVerticalTo(Row(0.5));
                WaitForChange(() => FirstVisible().ToString(), wasBar, 1200);
                DragIsLive("the scrollbar", bar, br.Left + br.Width / 2, br.Top + br.Height / 2,
                           br.Top + br.Height * 3 / 4);
            }

            // ...and the whole picture is a different one when the view mode changes under it.
            string wasMapMode = Colours(map);
            Menu("View", "Show Only Filtered Lines");
            WaitFiltered();
            WaitForChange(() => Colours(map), wasMapMode, 2000, poll: 60);
            Check("switching to filtered lines redraws the map", MapColours(map).Count > 0,
                  string.Join(" ", MapColours(map)));
            Shot("map-filtered");

            // With every line on show, the last screenful has almost no file below it - and the map used to
            // run out there, because the fill only ever walked forwards. It has to fill from the bottom up.
            // Ctrl+End rather than a row number: only the app knows exactly where the end is.
            Menu("View", "Focus Text Area");
            Wait(300);
            Keyboard.Pressing(VirtualKeyShort.CONTROL);
            string wasEnd = FirstVisible().ToString();
            Type(VirtualKeyShort.END);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            WaitForChange(() => FirstVisible().ToString(), wasEnd, 2500);
            using (var atEnd = Grab(map))
            {
                Say($"at the end of the file, view {FirstVisible()}");
                // Where exactly the rectangle lands is asserted to the pixel in the self-test, which can ask
                // the control. Here it is only worth knowing the map does not run out - a one-pixel outline
                // is not something a screenshot of the real thing can pick out from 160 filters' colours.
                Check("at the end of the file the map is still drawn the whole way down", BottomIsDrawn(atEnd),
                      $"bottom eighth of {atEnd.Height}px is blank");
            }
            Shot("map-end-of-file");

            // Back to the top: Ctrl+End left the caret on the last line of 33 million, and the stage after
            // this one searches forward from wherever the caret is.
            Keyboard.Pressing(VirtualKeyShort.CONTROL);
            string wasHome = FirstVisible().ToString();
            Type(VirtualKeyShort.HOME);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            WaitForChange(() => FirstVisible().ToString(), wasHome, 1500);

            Menu("View", "Show Only Filtered Lines");
            WaitFiltered();
        }

        Menu("View", "Show Match Map");
        Wait(800);
        Check("turning it off leaves the scrollbar behind", MapElement() is null && _app.VerticalScrollerName().Length > 0,
              _app.VerticalScrollerName());
        Menu("View", "Show Match Map");
        Wait(800);
        Check("and back on returns the map", MapElement() is not null);
    }

    private AutomationElement? MapElement()
        => _app.Grid().FindAllChildren().FirstOrDefault(s => (s.Name ?? "") == "Minimap");

    /// <summary>The colour a filter paints its own rows, read from its row in the filter list. Taken as the
    /// most common colour along the row, because any single pixel might land on a letter.</summary>
    private Color? FilterRowColour(string contains)
    {
        var node = _app.FilterNode(contains);
        if (node is null) return null;
        try { node.AsTreeItem().Select(); } catch { /* only to bring it into view */ }
        Wait(400);
        var r = (_app.FilterNode(contains) ?? node).BoundingRectangle;
        if (r.Width <= 20 || r.Height <= 0) return null;
        using var strip = new Bitmap(r.Width, 1, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(strip))
            g.CopyFromScreen(r.Left, r.Top + r.Height / 2, 0, 0, new Size(r.Width, 1));

        var tally = new Dictionary<int, int>();
        for (int x = 0; x < strip.Width; x++)
        {
            int argb = strip.GetPixel(x, 0).ToArgb();
            tally[argb] = tally.TryGetValue(argb, out int n) ? n + 1 : 1;
        }
        var c = Color.FromArgb(tally.OrderByDescending(kv => kv.Value).First().Key);
        return c.R > 245 && c.G > 245 && c.B > 245 ? null : c;   // an unstyled row is the plain background
    }

    private bool MapHasExactly(AutomationElement map, Color want)
    {
        using var bmp = Grab(map);
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 3; x < bmp.Width - 3; x++)
                if (bmp.GetPixel(x, y).ToArgb() == want.ToArgb()) return true;
        return false;
    }

    private static int Diff(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    /// <summary>What fraction of two grabs of the same control differ.</summary>
    private static double PictureDiff(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 1;
        int changed = 0, total = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 4; x < a.Width - 2; x++)
            {
                total++;
                if (Diff(a.GetPixel(x, y), b.GetPixel(x, y)) > 24) changed++;
            }
        return total == 0 ? 0 : (double)changed / total;
    }

    private static Bitmap Grab(AutomationElement e)
    {
        var r = e.BoundingRectangle;
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(bmp.Width, bmp.Height));
        return bmp;
    }

    /// <summary>The distinct colours the map is painting, ignoring the gutter it sits on and the viewport
    /// rectangle drawn over it - that rectangle is a tint of the selection colour across the full width, and
    /// counting it would mean the map never reads as blank.</summary>
    /// <summary>The map's colours as one reading, so a wait can watch it settle.</summary>
    private string Colours(AutomationElement map) => string.Join(" ", MapColours(map));

    /// <summary>How the rows are laid out, as one reading: how many and how tall. What word wrap changes.</summary>
    private string RowShape()
    {
        var rows = Rows();
        return rows.Length == 0 ? "none" : $"{rows.Length}@{rows.Max(r => r.BoundingRectangle.Height)}";
    }

    /// <summary>The preset names as one reading.</summary>
    private string PresetList() => string.Join("|", SafePresetNames());

    /// <summary>The top-level filters as one reading, for waits that follow an edit to the list.</summary>
    private string Roots() => string.Join("|", RootNames());

    /// <summary>
    /// The top-level filter names, read again if UI Automation refuses the first attempt.
    ///
    /// <para>These are read while the window is rebuilding the very list being walked - every caller is a
    /// keystroke that duplicates, undoes or redoes a filter - and UI Automation gives way there from time
    /// to time. Two consecutive nightly runs reported "An event was unable to invoke any of the subscribers
    /// (0x80040201)" out of this walk, in two DIFFERENT stages, which is the shape of a transient client
    /// error rather than a fault in the application. A rig that falls over on it files a finding against
    /// code that did nothing wrong, which is worse than useless.</para>
    ///
    /// <para>Tolerated, not swallowed: the last refusal is thrown if it never comes good, so a window that
    /// really has stopped answering still fails the stage and says why.</para>
    /// </summary>
    private string[] RootNames()
    {
        Exception? refused = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) Wait(150);
            try { return _app.RootFilterNames(); }
            catch (COMException ex) { refused = ex; }
        }
        throw refused!;
    }

    private List<string> MapColours(AutomationElement map)
    {
        using var bmp = Grab(map);
        var (skipTop, skipHeight) = ViewportRun(bmp);
        var seen = new List<Color>();
        for (int y = 2; y < bmp.Height - 2; y += 3)                                    // inside the frame
        {
            if (y >= skipTop && y < skipTop + skipHeight) continue;
            for (int x = 4; x < bmp.Width - 2; x++)                                    // past the rule down the left
            {
                var p = bmp.GetPixel(x, y);
                if (p.R > 230 && p.G > 230 && p.B > 230) continue;                     // gutter
                if (seen.Any(c => Math.Abs(c.R - p.R) + Math.Abs(c.G - p.G) + Math.Abs(c.B - p.B) < 90)) continue;
                seen.Add(p);
            }
        }
        return seen.Select(c => $"#{c.R:x2}{c.G:x2}{c.B:x2}").ToList();
    }

    /// <summary>The stretch of the map the viewport rectangle covers, so <see cref="MapColours"/> can leave
    /// it out: it is a tint of the selection colour laid over everything under it, and counting it would
    /// mean the map never reads as blank. Found down the last column as the longest run of anything that is
    /// not the gutter - which only holds when the map is mostly blank, and blank is when it is needed.</summary>
    private static (int Top, int Height) ViewportRun(Bitmap bmp)
    {
        int x = bmp.Width - 3;
        var tally = new Dictionary<int, int>();
        for (int y = 2; y < bmp.Height - 2; y++)
        {
            int argb = bmp.GetPixel(x, y).ToArgb();
            tally[argb] = tally.TryGetValue(argb, out int n) ? n + 1 : 1;
        }
        var gutter = Color.FromArgb(tally.OrderByDescending(kv => kv.Value).First().Key);

        int best = -1, bestRun = 0, run = 0;
        for (int y = 2; y < bmp.Height - 2; y++)
        {
            var p = bmp.GetPixel(x, y);
            bool painted = Math.Abs(p.R - gutter.R) + Math.Abs(p.G - gutter.G) + Math.Abs(p.B - gutter.B) > 24;
            if (painted) { run++; if (run > bestRun) { bestRun = run; best = y - run + 1; } }
            else run = 0;
        }
        return bestRun >= 4 ? (best, bestRun) : (-1, 0);
    }

    /// <summary>Whether the bottom eighth of the map has anything on it at all - which is where it used to
    /// run out, because the fill only ever walked forwards and there was no file left to walk through.</summary>
    private static bool BottomIsDrawn(Bitmap bmp)
    {
        var tally = new Dictionary<int, int>();
        for (int y = 2; y < bmp.Height - 2; y++)
            for (int x = 4; x < bmp.Width - 2; x += 3)
            {
                int argb = bmp.GetPixel(x, y).ToArgb();
                tally[argb] = tally.TryGetValue(argb, out int n) ? n + 1 : 1;
            }
        var gutter = Color.FromArgb(tally.OrderByDescending(kv => kv.Value).First().Key);

        for (int y = bmp.Height * 7 / 8; y < bmp.Height - 2; y++)
            for (int x = 4; x < bmp.Width - 2; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (Math.Abs(p.R - gutter.R) + Math.Abs(p.G - gutter.G) + Math.Abs(p.B - gutter.B) > 24) return true;
            }
        return false;
    }

    private void FindEverything()
    {
        Menu("View", "Focus Text Area");
        Wait(300);
        CtrlF();
        var bar = _app.FindBar();
        Check("Ctrl+F opens the bar", bar is not null, DescribePanes());
        if (bar is null) return;
        var edit = _app.FindInput();
        Check("with the keyboard in its box", Focused(edit), FocusedName());

        long top = FirstVisible();
        _app.SetText(edit, "");
        Wait(200);
        Type(BigFixture.EveryLineTerm);
        Wait(1500);
        Check("typing does not move the view", FirstVisible() == top, $"{top} -> {FirstVisible()}");
        Shot("find-typing");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Type(VirtualKeyShort.RETURN);
        Wait(300);
        while ((Tally().Length == 0 || Tally().Contains('+') || Tally() == "Searching\u2026") && sw.ElapsedMilliseconds < 30000)
            Wait(100);
        Say($"first search + full sweep: {sw.ElapsedMilliseconds} ms -> {Tally()}");
        Check("the counts settle without a plus", !Tally().Contains('+'), Tally());
        Check("and say where we are", Tally().StartsWith("Match ", StringComparison.Ordinal), Tally());
        Shot("find-found");

        string at = $"line {Caret()}";
        for (int i = 0; i < 10; i++) { Type(VirtualKeyShort.RETURN); Wait(70); }
        Wait(1000);
        Say($"after ten repeats: {$"line {Caret()}"} (was {at}), {Tally()}");
        Check("ten repeats moved the caret on", $"line {Caret()}" != at, $"{at} -> {$"line {Caret()}"}");

        // Ctrl+F while the box already has the keyboard: typing must replace the term.
        CtrlF();
        Wait(300);
        Type(BigFixture.SparseTerm);
        Wait(500);
        Check("Ctrl+F selects the term so a new one types straight over it", _app.TextOf(edit) == BigFixture.SparseTerm, _app.TextOf(edit));

        // Click the log, then Ctrl+F must come back to the box. Well down the view: the find bar is hosted
        // at the top of it, so a click near the top lands on the bar rather than on a line.
        ClickRow(0.75);
        ShotScreen("find-after-log-click");
        Check("clicking the log takes the keyboard out of the box", !Focused(edit), FocusedName());
        CtrlF();
        Wait(400);
        Check("Ctrl+F brings it back", Focused(edit), FocusedName());
        Type("x");
        Wait(300);
        Check("and had the whole term selected", _app.TextOf(edit) == "x", _app.TextOf(edit));

        // History.
        _app.SetText(edit, "");
        Wait(200);
        Type(VirtualKeyShort.DOWN);
        Wait(800);
        Say($"after Down in an empty box: '{_app.TextOf(edit)}'");
        Check("Down recalls the most recent term", _app.TextOf(edit).Length > 0, _app.TextOf(edit));
        ShotScreen("find-history");
        Type(VirtualKeyShort.ESCAPE);
        Wait(400);

        // Esc is one gesture on purpose: the bar goes, and the term, marks and counts go with it. A search
        // still running with nothing on screen to say so is the state the bar exists to remove.
        _app.SetText(edit, BigFixture.SparseTerm);
        Wait(200);
        string beforeSparse = Tally();
        Type(VirtualKeyShort.RETURN);
        WaitForChange(Tally, beforeSparse, 3000);
        string beforeEsc = Tally();
        Type(VirtualKeyShort.ESCAPE);
        WaitForChange(Tally, beforeEsc, 600);
        Check("Esc closes the bar", _app.FindBar() is null or { IsOffscreen: true });
        Check("and takes the counts with it", Tally().Length == 0, Tally());

        Menu("View", "Focus Text Area");
        Wait(300);
        for (int i = 0; i < 4; i++) { Type(VirtualKeyShort.DOWN); Wait(250); }

        // Hiding and showing must move the split. The date is on every line, so half the hits are on lines
        // the enabled filter is not showing.
        CtrlF();
        Wait(400);
        var box = _app.FindBar() is null ? null : _app.FindInput();
        if (box is not null)
        {
            _app.SetText(box, BigFixture.EveryLineDate);
            Wait(300);
            string beforeDate = Tally();
            Type(VirtualKeyShort.RETURN);
            WaitForChange(Tally, beforeDate, 6000);
        }
        string dim = Tally();
        Check("the counts never read as a bare number",
              dim.Length > 0 && !long.TryParse(dim.Replace(",", ""), out _), dim);
        Menu("View", "Show Only Filtered Lines");
        WaitForChange(Tally, dim, 4000);
        string hidden = Tally();
        Say($"counts one way '{dim}' -> the other '{hidden}'");
        Check("hiding the rest changes what the counts say", hidden != dim, $"{dim} -> {hidden}");
        Check("and exactly one of the two accounts for hidden matches",
              dim.Contains("hidden") != hidden.Contains("hidden"), $"{dim} / {hidden}");
        Shot("find-filtered-counts");
        Menu("View", "Show Only Filtered Lines");
        WaitForChange(Tally, hidden, 4000);
        Check("and showing them again changes it back", Tally() != hidden, $"{hidden} -> {Tally()}");

        string beforeClear = Tally();
        Type(VirtualKeyShort.ESCAPE);
        WaitForChange(Tally, beforeClear, 800);
        Check("Esc puts the term away", Tally().Length == 0, Tally());
        Shot("find-cleared");
    }

    private void UndoRedo()
    {
        if (!ClickFilterRow(BigFixture.HugeFilter)) { Check("a filter to work on", false); return; }
        Wait(600);
        int before = RootNames().Length;
        Say($"roots before: {before}");

        string wasRoots = Roots();
        Chord(VirtualKeyShort.KEY_D);
        WaitForChange(Roots, wasRoots, 2500);
        int after = RootNames().Length;
        Check("Ctrl+D duplicates the filter", after == before + 1, $"{before} -> {after}");
        Shot("undo-duplicated");

        string wasDuplicated = Roots();
        Chord(VirtualKeyShort.KEY_Z);
        WaitForChange(Roots, wasDuplicated, 2500);
        Check("Ctrl+Z takes it back", RootNames().Length == before,
              $"{RootNames().Length} vs {before}");

        string wasUndone = Roots();
        Chord(VirtualKeyShort.KEY_Y);
        WaitForChange(Roots, wasUndone, 2500);
        Check("Ctrl+Y puts it back", RootNames().Length == after,
              $"{RootNames().Length} vs {after}");

        string wasRedone = Roots();
        Chord(VirtualKeyShort.KEY_Z);
        WaitForChange(Roots, wasRedone, 2500);
        Check("and undo again leaves the list as it started", RootNames().Length == before,
              $"{RootNames().Length} vs {before}");
        Shot("undo");
    }

    private void Presets()
    {
        // Empty, the pane is a hint label rather than a list - and it invites a right-click, so that is
        // exactly where the first preset has to be reachable from.
        var pane = _app.Window.FindFirstDescendant(cf => cf.ByName("Filter presets"))
                   ?? _app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                          .FirstOrDefault(t => (t.Name ?? "").StartsWith("No presets yet", StringComparison.Ordinal));
        Check("the presets pane is there", pane is not null, DescribePanes());
        if (pane is null) return;

        var r = pane.BoundingRectangle;
        Mouse.MoveTo(new Point(r.Left + r.Width / 2, r.Top + Math.Min(60, r.Height / 2)));
        Wait(300);
        Click(MouseButton.Right);
        Wait(900);
        ShotScreen("presets-menu");

        var save = _app.DesktopChildren()
                       .SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)))
                       .FirstOrDefault(m => (m.Name ?? "").Contains("Save", StringComparison.OrdinalIgnoreCase));
        Check("right-clicking the empty pane offers to save a preset", save is not null, DescribeTopLevel());
        if (save is null) { Type(VirtualKeyShort.ESCAPE); return; }

        // Chosen with the keyboard: it opens a modal dialog, and a UIA Invoke that does that never returns.
        string beforeNaming = PresetList();
        Type(VirtualKeyShort.DOWN);
        Wait(200);
        Type(VirtualKeyShort.RETURN);
        Wait(1500);
        ShotScreen("presets-naming");
        Type(PresetName);
        Wait(400);
        Type(VirtualKeyShort.RETURN);
        WaitForChange(PresetList, beforeNaming, 1800);
        Say($"presets now: {string.Join(" | ", SafePresetNames())}");
        Check("the preset appears in the list", SafePresetNames().Any(n => n.Contains(PresetName, StringComparison.Ordinal)),
              string.Join("|", SafePresetNames()));
        Shot("presets");

        // Turning its filters off must clear it; clicking it must bring them back.
        if (ClickFilterRow(BigFixture.HugeFilter))
        {
            string wasEffect = _app.DescribePresets();
            _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
            WaitFiltered();
            WaitForChange(() => _app.DescribePresets(), wasEffect, 4000, poll: 50);
            Say($"after switching {BigFixture.HugeFilter} off: {Status()}");
            Say($"still ticked: {string.Join(" | ", TickedFilters())}");
            Check("switching its filters off drops the preset out of effect",
                  !_app.ActivePresets().Any(n => n.Contains(PresetName, StringComparison.Ordinal)), _app.DescribePresets());

            string wasTicked = _app.DescribePresets();
            _app.TickPreset(PresetName);
            WaitFiltered();
            WaitForChange(() => _app.DescribePresets(), wasTicked, 5000, poll: 50);
            Check("ticking it turns them back on", _app.ActivePresets().Any(n => n.Contains(PresetName, StringComparison.Ordinal)),
                  _app.DescribePresets());
            Check("and the view fills again", Rows().Length > 0, $"{Rows().Length} rows");
            Shot("presets-applied");
        }
    }

    private string[] SafePresetNames()
    {
        try { return _app.PresetNames(); } catch { return Array.Empty<string>(); }
    }

    /// <summary>Every filter row whose checkbox is on, wherever it is in the tree.</summary>
    private string[] TickedFilters()
    {
        try
        {
            return _app.Tree().FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                       .Where(t => t.Patterns.Toggle.PatternOrDefault?.ToggleState.ValueOrDefault == ToggleState.On)
                       .Select(t => t.Name ?? "")
                       .ToArray();
        }
        catch { return new[] { "(could not read)" }; }
    }

    private string DescribePanes()
        => string.Join(" ; ", _app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.List))
                                  .Select(p => $"List:'{p.Name}'"));

    private static void Chord(VirtualKeyShort key)
    {
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        Type(key);
        Keyboard.Release(VirtualKeyShort.CONTROL);
    }

    /// <summary>The exclude-precedence preference, changed the way a reader changes it. Nothing else in the
    /// suite can reach it: the menu item opens a modal, and a modal defeats both of the ways in that need no
    /// real input. UI Automation's Invoke on the item never returns from the dialog's message loop, and the
    /// stuck call then times out every other request; and an access key posted as a message is ignored,
    /// because injected messages leave the thread's key state alone so WinForms sees no Alt held. A real
    /// keyboard is the only way, which is exactly what this rig has.</summary>
    private void ExcludePrecedence()
    {
        Menu("View", "Focus Text Area");
        Wait(300);
        long before = ViewRows();

        Keyboard.Pressing(VirtualKeyShort.ALT);
        Type(VirtualKeyShort.KEY_E);
        Keyboard.Release(VirtualKeyShort.ALT);
        Wait(500);
        Type(VirtualKeyShort.KEY_R);
        Wait(1200);

        var dialog = _app.FindDialog("Preferences");
        Check("Alt+E, R opens Preferences", dialog is not null);
        if (dialog is null) return;

        var combo = CascadeApp.PrecedenceCombo(dialog);
        Check("Preferences offers the exclude-precedence choice", combo is not null,
              string.Join(" | ", dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
                                       .Select(CascadeApp.ComboText)));
        if (combo is null)
        {
            Type(VirtualKeyShort.ESCAPE);
            return;
        }

        string was = CascadeApp.ComboText(combo);
        combo.Focus();
        Wait(200);
        Type(VirtualKeyShort.DOWN);
        Wait(200);
        string now = CascadeApp.ComboText(combo);
        Check("the drop-down takes the keyboard and moves", now != was, $"{was} -> {now}");

        Type(VirtualKeyShort.RETURN);
        Wait(1500);
        Check("OK closes it", _app.FindDialog("Preferences") is null);

        // The set the sweep runs on scopes its exclude under the include above it, so nesting decides it
        // under either rule and the count is expected to hold. What is on trial here is that a change of
        // rule redraws the view rather than emptying it or leaving it mid-pass.
        long after = ViewRows();
        Check("the view still holds its lines after the rule changed", after == before, $"{before} -> {after}");

        // ...and back, so every stage after this one starts from the shipped default.
        Keyboard.Pressing(VirtualKeyShort.ALT);
        Type(VirtualKeyShort.KEY_E);
        Keyboard.Release(VirtualKeyShort.ALT);
        Wait(500);
        Type(VirtualKeyShort.KEY_R);
        Wait(1200);
        if (_app.FindDialog("Preferences") is { } again)
        {
            if (CascadeApp.PrecedenceCombo(again) is { } back)
            {
                back.Focus();
                Wait(200);
                Type(VirtualKeyShort.UP);
                Wait(200);
                Check("and it goes back the way it came", CascadeApp.ComboText(back) == was,
                      CascadeApp.ComboText(back));
            }
            Type(VirtualKeyShort.RETURN);
            Wait(1000);
        }
    }

    // ---- helpers ----

    private string Status() => Timed("status bar read", () => _app.AllStatusText());
    /// <summary>How many rows the view is showing, off the status bar's Fil: field.
    ///
    /// Every scroll target has to be a fraction of this. The sweep used to name row numbers taken from a
    /// 33-million-line trace; against any smaller file they all clamp to the end, and a check that meant
    /// "scroll somewhere else" then quietly measured the end of the file twice.</summary>
    private long ViewRows()
    {
        string s = _app.StatusText("Fil:");
        int at = s.IndexOf(':') + 1;
        return at > 0 && long.TryParse(s[at..].Replace(",", "").Trim(), out long v) && v > 0 ? v : BigFixture.Lines;
    }

    /// <summary>A row that far through the view, whatever is on show.</summary>
    private int Row(double fraction) => (int)(ViewRows() * fraction);

    /// <summary>What the find bar says it has found. Read off the bar itself: scanning every Text element
    /// for something ending in " lines" also matches the status bar's "Showing: all lines".</summary>
    private string Tally() => Timed("tally read", () => _app.FindBar() is null ? "" : _app.FindBarMessage());

    /// <summary>Every menu click, and every read of the view, through one place so they can be counted.</summary>
    private void Menu(params string[] path)
        => Timed("menu click", () => _app.ClickMenuOrThrow(path));

    private AutomationElement[] Rows() => Timed("rows read", () => _app.Rows());

    private long FirstVisible() => Timed("first visible line", () => _app.FirstVisibleLine());

    private long Caret() => Timed("caret line", () => _app.CaretLine());

    private void WaitIndexed()
    {
        WaitUntil(() => Status().Contains(BigFixture.TotalStatus), 90_000, poll: 100);
        WaitForStill(Status, 1500, poll: 50);
    }

    /// <summary>Waits for a filter pass to land. The status bar is the settle signal rather than the row
    /// count: it carries the progress and the counts, so it moves while the pass runs and stops when it is
    /// done, where a row count reaches its final value some time before the window has caught up.</summary>
    private void WaitFiltered()
    {
        WaitUntil(() => Rows().Length > 0, 90_000, poll: 100);
        WaitForStill(Status, 3000, poll: 50);
    }

    /// <summary>
    /// Clicks a row, counted as a fraction of however many the viewport is showing.
    ///
    /// <para>Never a row number: a maximised window on the screen this rig was written on holds 55 rows and
    /// a hosted runner's holds 27, so "row 30" is a click on one desktop and nothing at all on another. It
    /// used to say so and carry on, which is worse than clicking the wrong thing - the checks after it went
    /// on asserting about a click that never happened, and reported the app was at fault. The click has to
    /// land or this has to fail.</para>
    /// </summary>
    private void ClickRow(double downTheView)
    {
        var rows = Rows();
        int index = Math.Clamp((int)(rows.Length * downTheView), 0, rows.Length - 1);
        if (rows.Length == 0)
        {
            Check($"the view has rows to click {downTheView:P0} of the way down it", false, "no rows at all");
            return;
        }
        var r = rows[index].BoundingRectangle;
        var at = new Point(r.Left + 200, r.Top + r.Height / 2);
        Say($"ClickRow({downTheView:P0}) = row {index} of {rows.Length} at {at.X},{at.Y} " +
            $"(row {r.Left},{r.Top} {r.Width}x{r.Height})");
        Click(at);
        Wait(600);
    }

    private static void DoubleClick()
    {
        Click(MouseButton.Left);
        Wait(50);
        Click(MouseButton.Left);
    }

    private void CtrlF()
    {
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        Type(VirtualKeyShort.KEY_F);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        Wait(700);
    }

    /// <summary>The filter editor's pattern box, wherever the dialog is.
    ///
    /// <para>The main window is searched too, and has to be: the editor is owned by it, and UI Automation
    /// reports the box UNDER the main window rather than under a top-level window of its own - the dialog
    /// does not appear in <see cref="DescribeTopLevel"/> even while it is plainly open. Skipping the main
    /// window to avoid walking its rows was tried and stopped this finding the editor at all.</para></summary>
    private AutomationElement? FilterTextBox() => Timed("dialog hunt", () =>
        _app.DesktopChildren()
            .SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
            .FirstOrDefault(e => (e.Name ?? "") == "Filter text"));

    /// <summary>Takes the pointer off the window so a hover tip cannot outlive the stage that raised it.</summary>
    private void ParkPointer()
    {
        var w = Timed("window rect", () => _app.Window.BoundingRectangle);
        Mouse.Position = new Point(w.Left + 4, w.Top + 4);
        Wait(400);
    }

    /// <summary>
    /// Puts away any modal dialog left standing, and says so if one had to be forced.
    ///
    /// This matters more than it looks: a modal dialog belongs to the main window, so while one is up every
    /// later stage silently does nothing - menus will not open, shortcuts go to the dialog, and resizing
    /// throws. One stray dialog once cost eleven stages, reported as eleven unrelated faults.
    /// </summary>
    private void DismissDialogs()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (FilterTextBox() is null && Timed("dialog hunt", () => _app.DialogNow("Add Filter")) is null
                                        && Timed("dialog hunt", () => _app.DialogNow("Edit Filter")) is null)
                return;
            Type(VirtualKeyShort.ESCAPE);
            Wait(700);
        }
        Check("no dialog was left standing over the window", false, DescribeTopLevel());
    }

    /// <summary>Lets go of every key this rig holds down. <c>Keyboard.Press</c> sends key-DOWN and nothing
    /// else, so a modifier held across a stage that threw stays down for the rest of the DESKTOP session,
    /// not just the run - and a stuck Escape silently cancels every drag-and-drop, in this app and in every
    /// other. That is why the rig types keys rather than pressing them, and why this runs after each stage.</summary>
    private static void ReleaseKeys()
    {
        foreach (var key in new[] { VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.ALT, VirtualKeyShort.ESCAPE })
            try { Keyboard.Release(key); } catch { /* best effort */ }
    }

    private string CopyToClipboard()
    {
        RunSta(() => { try { System.Windows.Forms.Clipboard.Clear(); } catch { } });
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        Type(VirtualKeyShort.KEY_C);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        Wait(600);

        string text = "";
        RunSta(() =>
        {
            try { text = System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : ""; }
            catch { text = "<clipboard busy>"; }
        });
        return text;
    }

    private static void RunSta(Action a)
    {
        var t = new Thread(() => a());
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    private static string Trim(string s) => s.Length <= 70 ? s.Replace("\n", "\\n") : s[..70].Replace("\n", "\\n") + "...";

    private bool Focused(AutomationElement e)
    {
        try { return e.Properties.HasKeyboardFocus.ValueOrDefault; } catch { return false; }
    }

    private string FocusedName()
    {
        try { var f = _app.FocusedElement(); return $"{f.ControlType}:'{f.Name}'"; } catch { return "?"; }
    }

    private AutomationElement? TooltipWindow()
    {
        foreach (var w in _app.DesktopChildren())
            if (w.ControlType == ControlType.ToolTip && (w.Name ?? "").Length > 0) return w;
        return null;
    }

    private AutomationElement? ScrollBarElement()
    {
        foreach (var e in _app.Grid().FindAllDescendants())
            if (e.ControlType == ControlType.ScrollBar && (e.Name ?? "").Contains("Vertical")) return e;
        return null;
    }

    /// <summary>
    /// Holds the button down and moves in steps, sampling the view between moves. A repaint only happens when
    /// the message queue empties, and a held drag never lets it - so this is the one thing a screenshot after
    /// the gesture cannot tell you, and the one the eye notices immediately.
    /// </summary>
    private void DragIsLive(string what, AutomationElement target, int x, int fromY, int toY)
    {
        Mouse.MovePixelsPerMillisecond = 100;
        Mouse.Position = new Point(x, fromY);
        Wait(200);
        Mouse.Down(MouseButton.Left);
        Wait(150);
        long start = FirstVisible();
        var seen = new List<long>();
        int steps = 6;
        for (int i = 1; i <= steps; i++)
        {
            Mouse.Position = new Point(x, fromY + (toY - fromY) * i / steps);
            Wait(120);
            seen.Add(FirstVisible());   // still held
        }
        Mouse.Up(MouseButton.Left);
        Wait(600);
        long end = FirstVisible();
        Say($"dragging {what}: start {start}, during [{string.Join(", ", seen)}], after release {end}");
        Check($"dragging {what} moves the view while the button is still down",
              seen.Any(v => v != start), $"start {start}, during [{string.Join(", ", seen)}]");
        Check($"and dragging {what} keeps moving it the whole way down",
              seen.Distinct().Count() >= 3, $"[{string.Join(", ", seen)}]");
        Check($"and letting go of {what} does not jump somewhere else",
              seen.Count == 0 || Math.Abs(end - seen[^1]) <= Math.Max(64, Math.Abs(end) / 1000),
              $"during ended {seen.LastOrDefault()}, after release {end}");
    }

    private string DescribeTopLevel()
        => string.Join(" ; ", _app.DesktopChildren().Take(14).Select(w => $"{w.ControlType}:'{Trim(w.Name ?? "")}'"));

    private void Shot(string name) => Timed("screenshot", () =>
    {
        var r = _app.Window.BoundingRectangle;
        using var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try { PrintWindow(_app.Window.Properties.NativeWindowHandle.ValueOrDefault, hdc, 2); }
            finally { g.ReleaseHdc(hdc); }
        }
        bmp.Save(Path.Combine(Out, $"{_shot++:00}-{name}.png"), ImageFormat.Png);
    });

    /// <summary>Grabs the screen, for the things that live in their own window (tips, drop-downs).</summary>
    private void ShotScreen(string name) => Timed("screenshot", () =>
    {
        var r = _app.Window.BoundingRectangle;
        using var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
        bmp.Save(Path.Combine(Out, $"{_shot++:00}-{name}.png"), ImageFormat.Png);
    });

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(int value);
}

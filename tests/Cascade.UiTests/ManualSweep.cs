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

    private void Check(string what, bool ok, string detail = "")
    {
        _log.Add($"{(ok ? "ok  " : "BAD ")} {what}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
        if (!ok) _bugs.Add($"{what} :: {detail}");
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
        _app.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        Thread.Sleep(1500);
        _app.Activate();
        WaitIndexed();
        Say($"launched: {Status()}");

        string only = Environment.GetEnvironmentVariable("CASCADE_MANUAL_ONLY") ?? "";
        var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        void Stage(string name, Action run)
        {
            if (wanted.Length > 0 && name != "content" && !wanted.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return;
            Say($"===== {name} =====");
            try { run(); }
            catch (Exception ex) { Check($"{name} ran to the end", false, ex.Message); }
            finally
            {
                // Whatever a stage did to the window, the next one starts from the same place - and above
                // all with no modal dialog standing over it, since one of those makes every stage after it
                // quietly do nothing. A hover tip counts: it is a top-level window of its own, and while one
                // is up the main window reports no title, so the next stage finds nothing to drive.
                try
                {
                    ReleaseKeys();
                    ParkPointer();
                    DismissDialogs();
                    _app.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
                    Thread.Sleep(700);
                    _app.Activate();
                    Thread.Sleep(300);
                }
                catch { /* best effort */ }
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

        Say($"bugs found: {_bugs.Count}");
        Assert.True(true);
    }

    /// <summary>Markers draw down the map's left edge, so setting one has to change what it paints. Also on
    /// the scrollbar's trough, which is the only place a mark outside the map's window can appear.</summary>
    private void MarkersAndMap()
    {
        ClickRow(20);
        Thread.Sleep(400);
        var map = MapElement();
        Check("the map is there to draw on", map is not null);
        if (map is null) return;

        int before = MapPixels(map, Color.FromArgb(200, 40, 40));
        Chord(VirtualKeyShort.KEY_1);
        Thread.Sleep(1200);
        int after = MapPixels(map, Color.FromArgb(200, 40, 40));
        Say($"marker pixels on the map: {before} -> {after}");
        Check("setting a marker shows up on the map", after > before, $"{before} -> {after}");
        Shot("markers");

        // ...and walking to it must work.
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        Chord(VirtualKeyShort.HOME);
        Thread.Sleep(600);
        string at = $"line {_app.CaretLine()}";
        Keyboard.Type(VirtualKeyShort.KEY_1);
        Thread.Sleep(900);
        Check("pressing 1 walks to the marked line", $"line {_app.CaretLine()}" != at, $"{at} -> {$"line {_app.CaretLine()}"}");

        Chord(VirtualKeyShort.KEY_1);   // clear it again
        Thread.Sleep(800);
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
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);

        void OpenGoTo()
        {
            Keyboard.Pressing(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_G);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(900);
        }

        long Go(string typed)
        {
            OpenGoTo();
            var dlg = _app.FindDialog("Go To Line");
            if (dlg is null) return -1;
            var box = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
            if (box is null) { Keyboard.Type(VirtualKeyShort.ESCAPE); return -1; }
            box.Focus();
            Keyboard.Pressing(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_A);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Keyboard.Type(typed);
            Thread.Sleep(200);
            Keyboard.Type(VirtualKeyShort.RETURN);
            Thread.Sleep(1500);
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
              $"caret {deep}, top of view {_app.FirstVisibleLine()}");
        Shot("goto-deep");

        // A number past the end of the file must land at the end rather than nowhere.
        long past = Go((BigFixture.Lines * 10L).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Say($"Go To {BigFixture.Lines * 10L:N0} landed on {past}");
        Check("a line number past the end stops at the last line",
              past > BigFixture.Lines - 100 && past <= BigFixture.Lines, past.ToString());

        // With only matching lines on show, a hidden number cannot be gone to - it lands on the nearest
        // line that is shown, which for line 1 is whatever the top of the file has become.
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        Keyboard.Type(VirtualKeyShort.HOME);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        Thread.Sleep(1200);
        long top = CaretLine();
        long firstShown = Go("1");
        Say($"Go To 1 landed on {firstShown}; the first shown line is {top}");
        Check("a number below the first line on show lands on that line", firstShown == top,
              $"went to {firstShown}, first shown is {top}");

        // Zoom: the text changes size, the status says so, and it comes back.
        string zoom = _app.StatusText("Zoom:");
        _app.ClickMenuOrThrow("View", "Zoom In");
        Thread.Sleep(600);
        _app.ClickMenuOrThrow("View", "Zoom In");
        Thread.Sleep(600);
        string bigger = _app.StatusText("Zoom:");
        Say($"zoom {zoom} -> {bigger}");
        Check("zooming in says so in the status bar", bigger != zoom, $"{zoom} -> {bigger}");
        int rows = _app.Rows().Length;
        _app.ClickMenuOrThrow("View", "Reset Zoom");
        Thread.Sleep(800);
        Check("and resetting puts it back", _app.StatusText("Zoom:") == zoom,
              $"{_app.StatusText("Zoom:")}, was {zoom}");
        Check("and fewer lines fitted while it was bigger", rows < _app.Rows().Length,
              $"{rows} rows zoomed in, {_app.Rows().Length} at normal size");
        Shot("zoom-reset");
    }

    /// <summary>The line the caret is on. It used to be read out of the status bar's "Ln: X / Total", and
    /// when that went the parsing stayed behind and answered -1 to everything.</summary>
    private long CaretLine() => _app.CaretLine();

    private void PresetRoundTrip()    {
        var names = SafePresetNames();
        if (names.Length == 0)
        {
            _app.ClickMenuOrThrow("Filters", "Presets");
            Thread.Sleep(400);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Thread.Sleep(300);
            // Make one from the pane instead.
            var hint = _app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                           .FirstOrDefault(t => (t.Name ?? "").StartsWith("No presets yet", StringComparison.Ordinal));
            if (hint is null) { Check("somewhere to make a preset", false); return; }
            var hr = hint.BoundingRectangle;
            Mouse.MoveTo(new Point(hr.Left + hr.Width / 2, hr.Top + 40));
            Mouse.Click(MouseButton.Right);
            Thread.Sleep(800);
            Keyboard.Type(VirtualKeyShort.DOWN);
            Thread.Sleep(200);
            Keyboard.Type(VirtualKeyShort.RETURN);
            Thread.Sleep(1200);
            Keyboard.Type("round trip");
            Thread.Sleep(300);
            Keyboard.Type(VirtualKeyShort.RETURN);
            Thread.Sleep(1200);
        }
        names = SafePresetNames();
        Check("there is a preset to save", names.Length > 0, string.Join("|", names));
        if (names.Length == 0) return;

        Chord(VirtualKeyShort.KEY_S);
        Thread.Sleep(2500);
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
        Thread.Sleep(300);
        Keyboard.Type(BigFixture.EveryLineTerm);   // typed, as a user would
        Thread.Sleep(1500);
        int typed = MarkedPixels();
        Say($"marks while the bar is open: {typed}");

        // The bar stays open throughout: Esc would close it and drop the marks with it, which is the one
        // thing that would make "do the marks survive wrapping" unanswerable.
        Check("the marks are there before wrapping", typed > 200, $"{typed} marked pixels");
        _app.ClickMenuOrThrow("View", "Word Wrap");
        Thread.Sleep(1800);
        int wrapped = MarkedPixels();
        Say($"marked pixels flat {typed} -> wrapped {wrapped}");
        Check("the marks survive wrapping", wrapped > 200, $"{typed} -> {wrapped}");
        Shot("wrap-with-marks");

        // Select text on a wrapped row's SECOND segment: the hit test has to know which segment it is on.
        var tall = _app.Rows().FirstOrDefault(x => x.BoundingRectangle.Height > 60);
        if (tall is not null)
        {
            var tr = tall.BoundingRectangle;
            int y = tr.Top + tr.Height - 10;
            Mouse.MoveTo(new Point(tr.Left + 200, y));
            Mouse.Down(MouseButton.Left);
            Mouse.MoveTo(new Point(tr.Left + 330, y));
            Thread.Sleep(150);
            Mouse.Up(MouseButton.Left);
            Thread.Sleep(500);
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

        _app.ClickMenuOrThrow("View", "Word Wrap");
        Thread.Sleep(1200);
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(600);
    }

    /// <summary>Takes the window down to a size where the lines really do have to wrap.</summary>
    private void Narrow(int w, int h)
    {
        _app.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        Thread.Sleep(700);
        var t = _app.Window.Patterns.Transform.PatternOrDefault;
        t?.Move(80, 60);
        t?.Resize(w, h);
        Thread.Sleep(600);
        _app.Activate();
        Thread.Sleep(800);
        Say($"narrowed to {_app.Window.BoundingRectangle.Width}x{_app.Window.BoundingRectangle.Height}");
    }

    private void TooltipCanBeTurnedOff()
    {
        _app.ClickMenuOrThrow("View", "Show Matching Filters on Hover");
        Thread.Sleep(500);
        var rows = _app.Rows();
        if (rows.Length < 4) { Check("rows to hover", false); return; }
        var r = rows[3].BoundingRectangle;
        Mouse.MoveTo(new Point(r.Left + 400, r.Top + r.Height / 2));
        Thread.Sleep(1600);
        Check("turned off, hovering says nothing", TooltipWindow() is null, DescribeTopLevel());

        _app.ClickMenuOrThrow("View", "Show Matching Filters on Hover");
        Thread.Sleep(500);
        Mouse.MoveTo(new Point(r.Left + 200, r.Top + r.Height / 2));
        Thread.Sleep(1600);
        Check("and turned back on it speaks again", TooltipWindow() is not null, DescribeTopLevel());
        Mouse.MoveTo(new Point(r.Left + 200, r.Top - 250));
        Thread.Sleep(600);
    }

    private void UndoMenuWording()
    {
        var node = _app.FilterNode(BigFixture.MidFilter) ?? _app.FilterNode(BigFixture.HugeFilter);
        if (node is null) { Check("a filter to edit", false); return; }
        if (!ClickFilterRow(BigFixture.MidFilter) && !ClickFilterRow(BigFixture.HugeFilter)) { Check("the filter is reachable", false); return; }
        Thread.Sleep(500);
        Chord(VirtualKeyShort.KEY_D);
        Thread.Sleep(2000);

        var edit = OpenMenu("Edit");
        string undo = edit?.FirstOrDefault(m => (m.Name ?? "").StartsWith("Undo", StringComparison.Ordinal))?.Name ?? "";
        Say($"Edit menu undo item: '{undo}'");
        Check("the undo item names what it will take back", undo.Length > "Undo".Length, undo);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);

        if (!ClickFilterRow(BigFixture.MidFilter)) ClickFilterRow(BigFixture.HugeFilter);
        Thread.Sleep(400);
        Chord(VirtualKeyShort.KEY_Z);
        Thread.Sleep(2000);
    }

    /// <summary>Puts a filter row on screen and clicks it, so the list has focus and that row is selected.
    /// Scrolled out of sight, a row's rectangle is empty and a click on it lands on the menu bar - and the
    /// list is deliberately left where the user put it, so it will not scroll itself back.</summary>
    private bool ClickFilterRow(string contains)
    {
        var node = _app.FilterNode(contains);
        if (node is null) return false;
        try { node.AsTreeItem().Select(); } catch { /* selecting is only to scroll it into view */ }
        Thread.Sleep(400);
        var nr = (_app.FilterNode(contains) ?? node).BoundingRectangle;
        if (nr.Width <= 0 || nr.Height <= 0) { Say($"  ({contains} is still not on screen)"); return false; }
        Mouse.Click(new Point(nr.Left + 40, nr.Top + nr.Height / 2));
        Thread.Sleep(400);
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
            _app.UntickPreset(names[0]);
            Thread.Sleep(3000);
        }
        Check("the preset starts out of effect",
              !_app.ActivePresets().Any(n => n.StartsWith(names[0].Split(' ')[0], StringComparison.Ordinal)),
              _app.DescribePresets());

        // Past the leading square: a press there is the tick box, and would switch the preset's filters on.
        var r = item.BoundingRectangle;
        var onLabel = new Point(r.Left + r.Height + 30, r.Top + r.Height / 2);
        Mouse.Click(onLabel);
        Thread.Sleep(500);
        Check("clicking a preset's name does not put it in effect",
              !_app.ActivePresets().Any(n => n.StartsWith(names[0].Split(' ')[0], StringComparison.Ordinal)),
              _app.DescribePresets());

        // F2 renames.
        Keyboard.Type(VirtualKeyShort.F2);
        Thread.Sleep(1200);
        ShotScreen("preset-rename");
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        Keyboard.Type(VirtualKeyShort.KEY_A);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        Keyboard.Type("renamed one");
        Thread.Sleep(300);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(1200);
        Say($"after rename: {string.Join(" | ", SafePresetNames())}");
        Check("F2 renames a preset", SafePresetNames().Any(n => n.Contains("renamed one")),
              string.Join("|", SafePresetNames()));

        // Delete removes it.
        Mouse.Click(onLabel);
        Thread.Sleep(400);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Thread.Sleep(1200);
        Say($"after delete: {string.Join(" | ", SafePresetNames())}");
        Check("Delete removes it", !SafePresetNames().Any(n => n.Contains("renamed one")),
              string.Join("|", SafePresetNames()));
        Shot("preset-edited");
    }

    /// <summary>Ctrl+N has to carry the selected part of the line, not the whole of it.</summary>
    private void FilterFromSelection()
    {
        var rows = _app.Rows();
        if (rows.Length < 6) { Check("rows to select in", false); return; }
        var r = rows[4].BoundingRectangle;
        int y = r.Top + r.Height / 2;

        // Dragged, not double-clicked: a double-click opens the editor by itself, which would make Ctrl+N
        // look as though it had worked whatever it did.
        Mouse.MoveTo(new Point(r.Left + 300, y));
        Mouse.Down(MouseButton.Left);
        Mouse.MoveTo(new Point(r.Left + 420, y));
        Thread.Sleep(150);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(500);
        string picked = CopyToClipboard();
        Say($"selected text for the filter: '{Trim(picked)}'");
        Check("there is a selection to carry", picked.Length > 0, $"'{Trim(picked)}'");

        Chord(VirtualKeyShort.KEY_N);
        Thread.Sleep(1800);
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
        _app.ClickMenuOrThrow("View", "Show Only Filtered Lines");
        Thread.Sleep(3000);
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        CtrlF();
        if (_app.FindBar() is null) { Check("the bar opened", false, DescribePanes()); return; }
        var edit = _app.FindInput();

        _app.SetText(edit, BigFixture.SparseTerm);
        Thread.Sleep(300);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(3000);
        string first = $"line {_app.CaretLine()}";
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(1500);
        string second = $"line {_app.CaretLine()}";
        Check("Enter goes forwards", second != first, $"{first} -> {second}");

        Keyboard.Pressing(VirtualKeyShort.SHIFT);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Keyboard.Release(VirtualKeyShort.SHIFT);
        Thread.Sleep(1500);
        Check("Shift+Enter goes back", $"line {_app.CaretLine()}" == first,
              $"{second} -> {$"line {_app.CaretLine()}"} (wanted {first})");

        var regex = _app.FindBar()?.FindFirstDescendant(cf => cf.ByName("Regex"))?.AsCheckBox();
        Check("there is a regex option", regex is not null);
        if (regex is not null)
        {
            regex.IsChecked = true;
            _app.SetText(edit, BigFixture.RegexTerm);
            Thread.Sleep(400);
            Keyboard.Type(VirtualKeyShort.RETURN);
            Thread.Sleep(4000);
            Say($"regex search: {Tally()}");
            Check("a regex search finds something", Tally().StartsWith("Match ", StringComparison.Ordinal), Tally());

            // ...and one that cannot match must say so, or the regex is not really being used.
            _app.SetText(edit, BigFixture.ImpossibleRegexTerm);
            Thread.Sleep(400);
            Keyboard.Type(VirtualKeyShort.RETURN);
            Thread.Sleep(6000);
            Say($"impossible regex: {Tally()}");
            Check("and one that cannot match says so", Tally() == "No matches", Tally());
            regex.IsChecked = false;
        }
        Shot("backwards");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(600);
        _app.ClickMenuOrThrow("View", "Show Only Filtered Lines");   // back as the stage found it
        Thread.Sleep(3000);
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
    }

    /// <summary>The marks themselves, counted off the screen - nothing else can tell whether the term is
    /// actually shown as marked.</summary>
    private void FindHighlighting()
    {
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        int plain = MarkedPixels();
        Check("nothing is marked to begin with", plain < 200, $"{plain} marked pixels");

        CtrlF();
        if (_app.FindBar() is null) { Check("the bar opened", false, DescribePanes()); return; }
        var edit = _app.FindInput();
        _app.SetText(edit, "");
        Thread.Sleep(200);
        Keyboard.Type(BigFixture.EveryLineTerm);
        Thread.Sleep(1500);
        int typed = MarkedPixels();
        Say($"marked pixels: {plain} -> {typed} on typing alone");
        Check("typing alone marks what is on screen", typed > plain + 500, $"{plain} -> {typed}");
        Shot("highlight-typed");

        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(2500);
        int found = MarkedPixels(current: true);
        Check("the line the search landed on is marked more strongly", found > 30, $"{found} strong pixels");
        Shot("highlight-found");

        // The map has to let go of the marks on the same keypress that drops the term. It decides whether
        // it has anything to redraw by comparing the hit count it last drew against the document's, so
        // being repainted before the sweep was released left it holding them until something else happened
        // to invalidate the view - a click, a scroll, anything. Nothing is touched between these two grabs.
        var map = MapElement();
        using var withHits = map is null ? null : Grab(map);
        Keyboard.Type(VirtualKeyShort.ESCAPE);   // closes the bar and drops the term in one gesture
        Thread.Sleep(800);
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
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);
    }

    private AutomationElement[]? OpenMenu(string name)
    {
        var bar = _app.Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar));
        var top = bar?.FindAllChildren(cf => cf.ByControlType(ControlType.MenuItem))
                     .FirstOrDefault(m => (m.Name ?? "") == name);
        if (top is null) return null;
        var r = top.BoundingRectangle;
        Mouse.Click(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
        Thread.Sleep(700);
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
        Thread.Sleep(300);
        _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);   // the subtree, so plenty matches
        WaitFiltered();
        Say($"after enabling {BigFixture.HugeFilter}: {Status()}");
        Check("enabling it fills the view", _app.Rows().Length > 0, $"{_app.Rows().Length} rows");
        Check("and the count is no longer zero", !Status().Contains("Fil: 0"), Status());
        Shot("content");
    }

    private void FilterTooltip()
    {
        var rows = _app.Rows();
        if (rows.Length < 4) { Check("there are rows to hover", false); return; }

        var r = rows[3].BoundingRectangle;
        Mouse.MoveTo(new Point(r.Left + 250, r.Top + r.Height / 2));
        Thread.Sleep(1500);
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
        Thread.Sleep(1000);
        Check("moving off the line takes the tip away", TooltipWindow() is null, DescribeTopLevel());
    }

    private void CharacterSelection()
    {
        var rows = _app.Rows();
        if (rows.Length < 6) { Check("there are rows to select in", false); return; }
        var r = rows[4].BoundingRectangle;
        int y = r.Top + r.Height / 2;

        Mouse.MoveTo(new Point(r.Left + 200, y));
        Mouse.Down(MouseButton.Left);
        Mouse.MoveTo(new Point(r.Left + 330, y));
        Thread.Sleep(150);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(500);
        Shot("selection-drag");

        string dragged = CopyToClipboard();
        Say($"dragged copy: '{Trim(dragged)}'");
        Check("dragging inside a line copies just that text",
              dragged.Length > 0 && !dragged.Contains('\n'), $"'{Trim(dragged)}'");

        // A double-click in the log does NOT select a word: that gesture was deliberately given to writing
        // a filter for the line, carrying whatever was picked out. Leaving the dialog it opens standing is
        // what used to wreck every stage after this one, so it is put away before anything else happens.
        Mouse.MoveTo(new Point(r.Left + 260, y));
        Thread.Sleep(400);
        Mouse.DoubleClick(MouseButton.Left);
        Thread.Sleep(1500);
        ShotScreen("selection-double");
        var carried = FilterTextBox();
        Say($"double-click opened the editor with: '{Trim(carried is null ? "" : _app.TextOf(carried))}'");
        Check("double-clicking a line offers to make a filter from it", carried is not null, DescribeTopLevel());
        if (carried is not null)
            Check("carrying the text that was picked out", _app.TextOf(carried) == dragged.Trim(),
                  $"'{Trim(_app.TextOf(carried))}' vs '{Trim(dragged)}'");
        DismissDialogs();

        // A plain click takes the whole line, which is the only way to select one now.
        Mouse.Click(new Point(r.Left + 260, y));
        Thread.Sleep(600);
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

        var before = _app.Rows();
        int beforeHeight = before.Length > 0 ? before[0].BoundingRectangle.Height : 0;
        int beforeCount = before.Length;
        bool hadHBar = _app.HasHorizontalScrollBar();
        Shot("wrap-off");

        _app.ClickMenuOrThrow("View", "Word Wrap");
        Thread.Sleep(1500);
        var after = _app.Rows();
        int tallest = after.Length > 0 ? after.Max(x => x.BoundingRectangle.Height) : 0;
        Say($"rows {beforeCount}@{beforeHeight}px -> {after.Length}, tallest {tallest}px, hbar {hadHBar} -> {_app.HasHorizontalScrollBar()}");
        Check("wrapping makes long lines taller", tallest > beforeHeight, $"{beforeHeight} -> {tallest}");
        Check("fewer lines fit", after.Length < beforeCount, $"{beforeCount} -> {after.Length}");
        Check("the sideways scrollbar goes", !_app.HasHorizontalScrollBar());
        Check("the rows still run down the window in order",
              after.Zip(after.Skip(1)).All(p => p.Second.BoundingRectangle.Top >= p.First.BoundingRectangle.Bottom - 2),
              string.Join(",", after.Select(x => x.BoundingRectangle.Top)));
        Shot("wrap-on");

        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        string at = $"line {_app.CaretLine()}";
        for (int i = 0; i < 3; i++) { Keyboard.Type(VirtualKeyShort.DOWN); Thread.Sleep(150); }
        Check("the caret still moves while wrapped", $"line {_app.CaretLine()}" != at, $"{at} -> {$"line {_app.CaretLine()}"}");

        // Clicking the lower half of a wrapped row must land on that row, not the one below.
        var rows = _app.Rows();
        var tall = rows.FirstOrDefault(x => x.BoundingRectangle.Height > beforeHeight * 1.5);
        if (tall is not null)
        {
            string want = tall.Patterns.LegacyIAccessible.Pattern.Value.ValueOrDefault;
            var tr = tall.BoundingRectangle;
            Mouse.Click(new Point(tr.Left + 200, tr.Bottom - 6));
            Thread.Sleep(500);
            Check("clicking low in a wrapped line selects that line",
                  _app.CaretLine().ToString(System.Globalization.CultureInfo.InvariantCulture) == want,
                  $"wanted {want}, got line {_app.CaretLine()}");
        }

        _app.ClickMenuOrThrow("View", "Word Wrap");
        Thread.Sleep(1200);
        Check("turning it off puts the rows back",
              _app.Rows() is { Length: > 0 } back && back[0].BoundingRectangle.Height == beforeHeight,
              $"{(_app.Rows() is { Length: > 0 } b2 ? b2[0].BoundingRectangle.Height : -1)} vs {beforeHeight}");
        Check("and brings the sideways scrollbar back", _app.HasHorizontalScrollBar() == hadHBar);
    }

    private void MatchMap()
    {
        Check("there is a scrollbar", _app.VerticalScrollerName().Length > 0, _app.VerticalScrollerName());
        Say($"scrollbar scale: {_app.ScrollBarScale()}");
        long first = _app.FirstVisibleLine();
        bool scrolled = _app.ScrollVerticalTo(Row(0.15));
        Check("and it scrolls the view", scrolled && _app.FirstVisibleLine() != first,
              $"{first} -> {_app.FirstVisibleLine()}");
        Shot("map");

        var map = MapElement();
        Check("the minimap is beside it, not instead of it", map is not null);
        if (map is not null)
        {
            // Nothing is coloured on the map that a filter is not colouring in the text. Switching them all
            // off is the sharpest form of that: the map has to go blank.
            _app.ScrollVerticalTo(150_000);
            Thread.Sleep(900);
            var coloured = MapColours(map);
            Say($"colours with the filters on: {string.Join(" ", coloured)}");
            Check("the map is coloured while filters are on", coloured.Count > 0, string.Join(" ", coloured));

            _app.ClickMenuOrThrow("Filters", "Disable All");
            WaitFiltered();
            Thread.Sleep(1500);
            var bare = MapColours(map);
            Say($"colours with every filter off: {string.Join(" ", bare)}");
            Check("and blank once none of them are", bare.Count == 0, string.Join(" ", bare));
            Shot("map-blank");

            if (ClickFilterRow(BigFixture.HugeFilter))
            {
                _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
                WaitFiltered();
                _app.ScrollVerticalTo(150_000);
                Thread.Sleep(1500);
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
            _app.ClickMenuOrThrow("View", "Show Only Filtered Lines");
            Thread.Sleep(3000);
            _app.ScrollVerticalTo(Row(0.15));
            Thread.Sleep(900);
            long highLine = _app.FirstVisibleLine();
            using var atHigh = Grab(map);
            _app.ScrollVerticalTo(Row(0.85));
            Thread.Sleep(900);
            long lowLine = _app.FirstVisibleLine();
            using var atLow = Grab(map);
            Say($"map across a jump from 15% to 85% of the file ({highLine} -> {lowLine}): " +
                $"{PictureDiff(atHigh, atLow):P0} of the pixels changed");
            Check("the map shows somewhere else entirely after a long scroll", PictureDiff(atHigh, atLow) > 0.10,
                  $"{PictureDiff(atHigh, atLow):P0} of the pixels changed, view {highLine} -> {lowLine}");
            _app.ClickMenuOrThrow("View", "Show Only Filtered Lines");
            Thread.Sleep(3000);
            // Put the second filter back off: the stages after this one share the window.
            if (ClickFilterRow(BigFixture.BusyFilter))
            {
                _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
                WaitFiltered();
            }

            // Clicking the map moves the view without the scrollbar going anywhere much: it is the fine
            // adjustment, and the file is far too long for a window of it to register on the whole scale.
            _app.ScrollVerticalTo(Row(0.5));
            Thread.Sleep(1200);
            var r = map.BoundingRectangle;
            long viewBefore = _app.FirstVisibleLine();
            Mouse.Click(new Point(r.Left + r.Width / 2, r.Top + r.Height / 6));
            Thread.Sleep(1200);
            Say($"clicking high on the map: {viewBefore} -> {_app.FirstVisibleLine()}");
            Check("clicking the map moves the view", _app.FirstVisibleLine() != viewBefore,
                  $"{viewBefore} -> {_app.FirstVisibleLine()}");
            Shot("map-viewport");

            DragIsLive("the minimap", map, r.Left + r.Width / 2, r.Top + r.Height / 4, r.Top + r.Height * 3 / 4);
            var bar = ScrollBarElement();
            if (bar is not null)
            {
                // Halfway down the view, so the thumb is halfway down the trough and the press lands on it
                // rather than paging.
                var br = bar.BoundingRectangle;
                _app.ScrollVerticalTo(Row(0.5));
                Thread.Sleep(1200);
                DragIsLive("the scrollbar", bar, br.Left + br.Width / 2, br.Top + br.Height / 2,
                           br.Top + br.Height * 3 / 4);
            }

            // ...and the whole picture is a different one when the view mode changes under it.
            _app.ClickMenuOrThrow("View", "Show Only Filtered Lines");
            WaitFiltered();
            Thread.Sleep(2000);
            Check("switching to filtered lines redraws the map", MapColours(map).Count > 0,
                  string.Join(" ", MapColours(map)));
            Shot("map-filtered");

            // With every line on show, the last screenful has almost no file below it - and the map used to
            // run out there, because the fill only ever walked forwards. It has to fill from the bottom up.
            // Ctrl+End rather than a row number: only the app knows exactly where the end is.
            _app.ClickMenuOrThrow("View", "Focus Text Area");
            Thread.Sleep(300);
            Keyboard.Pressing(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.END);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(2500);
            using (var atEnd = Grab(map))
            {
                Say($"at the end of the file, view {_app.FirstVisibleLine()}");
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
            Keyboard.Type(VirtualKeyShort.HOME);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(1500);

            _app.ClickMenuOrThrow("View", "Show Only Filtered Lines");
            WaitFiltered();
            Thread.Sleep(1500);
        }

        _app.ClickMenuOrThrow("View", "Show Match Map");
        Thread.Sleep(800);
        Check("turning it off leaves the scrollbar behind", MapElement() is null && _app.VerticalScrollerName().Length > 0,
              _app.VerticalScrollerName());
        _app.ClickMenuOrThrow("View", "Show Match Map");
        Thread.Sleep(800);
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
        Thread.Sleep(400);
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
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        CtrlF();
        var bar = _app.FindBar();
        Check("Ctrl+F opens the bar", bar is not null, DescribePanes());
        if (bar is null) return;
        var edit = _app.FindInput();
        Check("with the keyboard in its box", Focused(edit), FocusedName());

        long top = _app.FirstVisibleLine();
        _app.SetText(edit, "");
        Thread.Sleep(200);
        Keyboard.Type(BigFixture.EveryLineTerm);
        Thread.Sleep(1500);
        Check("typing does not move the view", _app.FirstVisibleLine() == top, $"{top} -> {_app.FirstVisibleLine()}");
        Shot("find-typing");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(300);
        while ((Tally().Length == 0 || Tally().Contains('+') || Tally() == "Searching\u2026") && sw.ElapsedMilliseconds < 30000)
            Thread.Sleep(100);
        Say($"first search + full sweep: {sw.ElapsedMilliseconds} ms -> {Tally()}");
        Check("the counts settle without a plus", !Tally().Contains('+'), Tally());
        Check("and say where we are", Tally().StartsWith("Match ", StringComparison.Ordinal), Tally());
        Shot("find-found");

        string at = $"line {_app.CaretLine()}";
        for (int i = 0; i < 10; i++) { Keyboard.Type(VirtualKeyShort.RETURN); Thread.Sleep(70); }
        Thread.Sleep(1000);
        Say($"after ten repeats: {$"line {_app.CaretLine()}"} (was {at}), {Tally()}");
        Check("ten repeats moved the caret on", $"line {_app.CaretLine()}" != at, $"{at} -> {$"line {_app.CaretLine()}"}");

        // Ctrl+F while the box already has the keyboard: typing must replace the term.
        CtrlF();
        Thread.Sleep(300);
        Keyboard.Type(BigFixture.SparseTerm);
        Thread.Sleep(500);
        Check("Ctrl+F selects the term so a new one types straight over it", _app.TextOf(edit) == BigFixture.SparseTerm, _app.TextOf(edit));

        // Click the log, then Ctrl+F must come back to the box. Well down the view: the find bar is modeless
        // and sits over the top-left of it.
        ClickRow(30);
        ShotScreen("find-after-log-click");
        Check("clicking the log takes the keyboard out of the box", !Focused(edit), FocusedName());
        CtrlF();
        Thread.Sleep(400);
        Check("Ctrl+F brings it back", Focused(edit), FocusedName());
        Keyboard.Type("x");
        Thread.Sleep(300);
        Check("and had the whole term selected", _app.TextOf(edit) == "x", _app.TextOf(edit));

        // History.
        _app.SetText(edit, "");
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.DOWN);
        Thread.Sleep(800);
        Say($"after Down in an empty box: '{_app.TextOf(edit)}'");
        Check("Down recalls the most recent term", _app.TextOf(edit).Length > 0, _app.TextOf(edit));
        ShotScreen("find-history");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);

        // Esc is one gesture on purpose: the bar goes, and the term, marks and counts go with it. A search
        // still running with nothing on screen to say so is the state the bar exists to remove.
        _app.SetText(edit, BigFixture.SparseTerm);
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(3000);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(600);
        Check("Esc closes the bar", _app.FindBar() is null or { IsOffscreen: true });
        Check("and takes the counts with it", Tally().Length == 0, Tally());

        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        for (int i = 0; i < 4; i++) { Keyboard.Type(VirtualKeyShort.DOWN); Thread.Sleep(250); }

        // Hiding and showing must move the split. The date is on every line, so half the hits are on lines
        // the enabled filter is not showing.
        CtrlF();
        Thread.Sleep(400);
        var box = _app.FindBar() is null ? null : _app.FindInput();
        if (box is not null)
        {
            _app.SetText(box, BigFixture.EveryLineDate);
            Thread.Sleep(300);
            Keyboard.Type(VirtualKeyShort.RETURN);
            Thread.Sleep(6000);
        }
        string dim = Tally();
        Check("the counts never read as a bare number",
              dim.Length > 0 && !long.TryParse(dim.Replace(",", ""), out _), dim);
        _app.ClickMenuOrThrow("View", "Show Only Filtered Lines");
        Thread.Sleep(4000);
        string hidden = Tally();
        Say($"counts one way '{dim}' -> the other '{hidden}'");
        Check("hiding the rest changes what the counts say", hidden != dim, $"{dim} -> {hidden}");
        Check("and exactly one of the two accounts for hidden matches",
              dim.Contains("hidden") != hidden.Contains("hidden"), $"{dim} / {hidden}");
        Shot("find-filtered-counts");
        _app.ClickMenuOrThrow("View", "Show Only Filtered Lines");
        Thread.Sleep(4000);
        Check("and showing them again changes it back", Tally() != hidden, $"{hidden} -> {Tally()}");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(800);
        Check("Esc puts the term away", Tally().Length == 0, Tally());
        Shot("find-cleared");
    }

    private void UndoRedo()
    {
        if (!ClickFilterRow(BigFixture.HugeFilter)) { Check("a filter to work on", false); return; }
        Thread.Sleep(600);
        int before = _app.RootFilterNames().Length;
        Say($"roots before: {before}");

        Chord(VirtualKeyShort.KEY_D);
        Thread.Sleep(2500);
        int after = _app.RootFilterNames().Length;
        Check("Ctrl+D duplicates the filter", after == before + 1, $"{before} -> {after}");
        Shot("undo-duplicated");

        Chord(VirtualKeyShort.KEY_Z);
        Thread.Sleep(2500);
        Check("Ctrl+Z takes it back", _app.RootFilterNames().Length == before,
              $"{_app.RootFilterNames().Length} vs {before}");

        Chord(VirtualKeyShort.KEY_Y);
        Thread.Sleep(2500);
        Check("Ctrl+Y puts it back", _app.RootFilterNames().Length == after,
              $"{_app.RootFilterNames().Length} vs {after}");

        Chord(VirtualKeyShort.KEY_Z);
        Thread.Sleep(2500);
        Check("and undo again leaves the list as it started", _app.RootFilterNames().Length == before,
              $"{_app.RootFilterNames().Length} vs {before}");
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
        Thread.Sleep(300);
        Mouse.Click(MouseButton.Right);
        Thread.Sleep(900);
        ShotScreen("presets-menu");

        var save = _app.DesktopChildren()
                       .SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)))
                       .FirstOrDefault(m => (m.Name ?? "").Contains("Save", StringComparison.OrdinalIgnoreCase));
        Check("right-clicking the empty pane offers to save a preset", save is not null, DescribeTopLevel());
        if (save is null) { Keyboard.Type(VirtualKeyShort.ESCAPE); return; }

        // Chosen with the keyboard: it opens a modal dialog, and a UIA Invoke that does that never returns.
        Keyboard.Type(VirtualKeyShort.DOWN);
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(1500);
        ShotScreen("presets-naming");
        Keyboard.Type(PresetName);
        Thread.Sleep(400);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(1800);
        Say($"presets now: {string.Join(" | ", SafePresetNames())}");
        Check("the preset appears in the list", SafePresetNames().Any(n => n.Contains(PresetName, StringComparison.Ordinal)),
              string.Join("|", SafePresetNames()));
        Shot("presets");

        // Turning its filters off must clear it; clicking it must bring them back.
        if (ClickFilterRow(BigFixture.HugeFilter))
        {
            _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
            Thread.Sleep(4000);
            Say($"after switching {BigFixture.HugeFilter} off: {Status()}");
            Say($"still ticked: {string.Join(" | ", TickedFilters())}");
            Check("switching its filters off drops the preset out of effect",
                  !_app.ActivePresets().Any(n => n.Contains(PresetName, StringComparison.Ordinal)), _app.DescribePresets());

            _app.TickPreset(PresetName);
            Thread.Sleep(5000);
            Check("ticking it turns them back on", _app.ActivePresets().Any(n => n.Contains(PresetName, StringComparison.Ordinal)),
                  _app.DescribePresets());
            Check("and the view fills again", _app.Rows().Length > 0, $"{_app.Rows().Length} rows");
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
        Keyboard.Type(key);
        Keyboard.Release(VirtualKeyShort.CONTROL);
    }

    // ---- helpers ----

    private string Status() => _app.AllStatusText();

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
    private string Tally() => _app.FindBar() is null ? "" : _app.FindBarMessage();

    private void WaitIndexed()
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until && !Status().Contains(BigFixture.TotalStatus)) Thread.Sleep(500);
        Thread.Sleep(1500);
    }

    private void WaitFiltered()
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until && _app.Rows().Length == 0) Thread.Sleep(500);
        Thread.Sleep(3000);
    }

    private void ClickRow(int index)
    {
        var rows = _app.Rows();
        if (rows.Length <= index) { Say($"ClickRow({index}): only {rows.Length} rows"); return; }
        var r = rows[index].BoundingRectangle;
        var at = new Point(r.Left + 200, r.Top + r.Height / 2);
        Say($"ClickRow({index}) at {at.X},{at.Y} (row {r.Left},{r.Top} {r.Width}x{r.Height})");
        Mouse.Click(at);
        Thread.Sleep(600);
    }

    private static void DoubleClick()
    {
        Mouse.Click(MouseButton.Left);
        Thread.Sleep(50);
        Mouse.Click(MouseButton.Left);
    }

    private void CtrlF()
    {
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        Keyboard.Type(VirtualKeyShort.KEY_F);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        Thread.Sleep(700);
    }

    /// <summary>The filter editor's pattern box, wherever the dialog is.</summary>
    private AutomationElement? FilterTextBox()
        => _app.DesktopChildren()
               .SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
               .FirstOrDefault(e => (e.Name ?? "") == "Filter text");

    /// <summary>Takes the pointer off the window so a hover tip cannot outlive the stage that raised it.</summary>
    private void ParkPointer()
    {
        var w = _app.Window.BoundingRectangle;
        Mouse.Position = new Point(w.Left + 4, w.Top + 4);
        Thread.Sleep(400);
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
            if (FilterTextBox() is null && _app.FindDialog("Add Filter") is null && _app.FindDialog("Edit Filter") is null)
                return;
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Thread.Sleep(700);
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
        Keyboard.Type(VirtualKeyShort.KEY_C);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        Thread.Sleep(600);

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
        Thread.Sleep(200);
        Mouse.Down(MouseButton.Left);
        Thread.Sleep(150);
        long start = _app.FirstVisibleLine();
        var seen = new List<long>();
        int steps = 6;
        for (int i = 1; i <= steps; i++)
        {
            Mouse.Position = new Point(x, fromY + (toY - fromY) * i / steps);
            Thread.Sleep(120);
            seen.Add(_app.FirstVisibleLine());   // still held
        }
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(600);
        long end = _app.FirstVisibleLine();
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

    private void Shot(string name)
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
    }

    /// <summary>Grabs the screen, for the things that live in their own window (tips, drop-downs).</summary>
    private void ShotScreen(string name)
    {
        var r = _app.Window.BoundingRectangle;
        using var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
        bmp.Save(Path.Combine(Out, $"{_shot++:00}-{name}.png"), ImageFormat.Png);
    }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(int value);
}

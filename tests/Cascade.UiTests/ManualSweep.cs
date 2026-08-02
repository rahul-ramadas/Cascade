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
/// TEMPORARY exploratory rig: drives the real app on the real 7.37 GB trace with the real filter set, using
/// actual mouse and keyboard, and writes findings plus screenshots. Gated on CASCADE_MANUAL=1.
/// </summary>
public class ManualSweep : IDisposable
{
    private const string Big = @"E:\Repos\test-file.txt";
    private const string RealFilters = @"E:\Scripts\Orders.cascade";
    private const string Out = @"E:\Temp\manual";
    private static readonly string Filters = Path.Combine(Out, "Orders.cascade");

    private readonly List<string> _log = new();
    private readonly List<string> _bugs = new();
    private CascadeApp _app = null!;
    private int _shot;

    public void Dispose()
    {
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
        if (Environment.GetEnvironmentVariable("CASCADE_MANUAL") != "1") return;
        SetProcessDpiAwarenessContext(-4);
        Directory.CreateDirectory(Out);
        // A copy, so saving can be exercised without touching the real thing.
        File.Copy(RealFilters, Filters, overwrite: true);

        _app = CascadeApp.LaunchExisting(Big, Filters, CascadeApp.NewSettingsDir(),
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
                // Whatever a stage did to the window, the next one starts from the same place.
                try
                {
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
        string at = _app.StatusText("Ln:");
        Keyboard.Type(VirtualKeyShort.KEY_1);
        Thread.Sleep(900);
        Check("pressing 1 walks to the marked line", _app.StatusText("Ln:") != at, $"{at} -> {_app.StatusText("Ln:")}");

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
    private void PresetRoundTrip()
    {
        var names = SafePresetNames();
        if (names.Length == 0)
        {
            _app.ClickMenuOrThrow("Filters", "Presets");
            Thread.Sleep(400);
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(300);
            // Make one from the pane instead.
            var hint = _app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                           .FirstOrDefault(t => (t.Name ?? "").StartsWith("No presets yet"));
            if (hint is null) { Check("somewhere to make a preset", false); return; }
            var hr = hint.BoundingRectangle;
            Mouse.MoveTo(new Point(hr.Left + hr.Width / 2, hr.Top + 40));
            Mouse.Click(MouseButton.Right);
            Thread.Sleep(800);
            Keyboard.Press(VirtualKeyShort.DOWN);
            Thread.Sleep(200);
            Keyboard.Press(VirtualKeyShort.RETURN);
            Thread.Sleep(1200);
            Keyboard.Type("round trip");
            Thread.Sleep(300);
            Keyboard.Press(VirtualKeyShort.RETURN);
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
        var dlg = _app.FindDialog("Find");
        if (dlg is null) { Check("the bar opened", false); return; }
        var edit = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!;
        _app.SetText(edit, "");
        Thread.Sleep(300);
        Keyboard.Type("OrderService");            // typed, as a user would
        Thread.Sleep(1500);
        int typed = MarkedPixels();
        Say($"marks while the bar is open: {typed}");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(600);

        int flat = MarkedPixels();
        Check("the marks are there before wrapping", flat > 200, $"open {typed}, closed {flat}");
        _app.ClickMenuOrThrow("View", "Word Wrap");
        Thread.Sleep(1800);
        int wrapped = MarkedPixels();
        Say($"marked pixels flat {flat} -> wrapped {wrapped}");
        Check("the marks survive wrapping", wrapped > 200, $"{flat} -> {wrapped}");
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
        Keyboard.Press(VirtualKeyShort.ESCAPE);
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
        var node = _app.FilterNode("[ORDER_SET_STATE]") ?? _app.FilterNode("[order-service]");
        if (node is null) { Check("a filter to edit", false); return; }
        if (!ClickFilterRow("[ORDER_SET_STATE]") && !ClickFilterRow("[order-service]")) { Check("the filter is reachable", false); return; }
        Thread.Sleep(500);
        Chord(VirtualKeyShort.KEY_D);
        Thread.Sleep(2000);

        var edit = OpenMenu("Edit");
        string undo = edit?.FirstOrDefault(m => (m.Name ?? "").StartsWith("Undo"))?.Name ?? "";
        Say($"Edit menu undo item: '{undo}'");
        Check("the undo item names what it will take back", undo.Length > "Undo".Length, undo);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);

        if (!ClickFilterRow("[ORDER_SET_STATE]")) ClickFilterRow("[order-service]");
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

        var r = item.BoundingRectangle;
        Mouse.Click(new Point(r.Left + 30, r.Top + r.Height / 2));
        Thread.Sleep(500);

        // F2 renames.
        Keyboard.Press(VirtualKeyShort.F2);
        Thread.Sleep(1200);
        ShotScreen("preset-rename");
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        Keyboard.Type(VirtualKeyShort.KEY_A);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        Keyboard.Type("renamed one");
        Thread.Sleep(300);
        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(1200);
        Say($"after rename: {string.Join(" | ", SafePresetNames())}");
        Check("F2 renames a preset", SafePresetNames().Any(n => n.Contains("renamed one")),
              string.Join("|", SafePresetNames()));

        // Delete removes it.
        Mouse.Click(new Point(r.Left + 30, r.Top + r.Height / 2));
        Thread.Sleep(400);
        Keyboard.Press(VirtualKeyShort.DELETE);
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
        Mouse.MoveTo(new Point(r.Left + 300, y));
        Thread.Sleep(300);
        Mouse.DoubleClick(MouseButton.Left);
        Thread.Sleep(500);
        string word = CopyToClipboard();
        Say($"selected word for the filter: '{Trim(word)}'");

        Chord(VirtualKeyShort.KEY_N);
        Thread.Sleep(1800);
        ShotScreen("newfilter");
        Say($"after Ctrl+N: {DescribeTopLevel()}");
        var box = _app.DesktopChildren()
                      .SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
                      .FirstOrDefault(e => (e.Name ?? "") == "Filter text");
        Check("Ctrl+N opens the filter editor", box is not null, DescribeTopLevel());
        if (box is not null)
        {
            string prefilled = _app.TextOf(box);
            Say($"prefilled with: '{Trim(prefilled)}'");
            Check("prefilled with the selection, not the whole line", prefilled == word.Trim(),
                  $"'{Trim(prefilled)}' vs '{Trim(word)}'");
        }
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(900);
    }

    private void FindBackwardsAndRegex()
    {
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        CtrlF();
        var dlg = _app.FindDialog("Find");
        if (dlg is null) { Check("the bar opened", false); return; }
        var edit = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!;

        _app.SetText(edit, "HCI_RegUpdateCOD");
        Thread.Sleep(300);
        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(3000);
        string first = _app.StatusText("Ln:");
        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(1500);
        string second = _app.StatusText("Ln:");
        Check("Enter goes forwards", second != first, $"{first} -> {second}");

        Keyboard.Pressing(VirtualKeyShort.SHIFT);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Keyboard.Release(VirtualKeyShort.SHIFT);
        Thread.Sleep(1500);
        Check("Shift+Enter goes back", _app.StatusText("Ln:") == first,
              $"{second} -> {_app.StatusText("Ln:")} (wanted {first})");

        var regex = dlg.FindFirstDescendant(cf => cf.ByName("Regex"))?.AsCheckBox();
        Check("there is a regex option", regex is not null);
        if (regex is not null)
        {
            regex.IsChecked = true;
            _app.SetText(edit, "HCI_RegUpdate[A-Z]+");
            Thread.Sleep(400);
            Keyboard.Press(VirtualKeyShort.RETURN);
            Thread.Sleep(4000);
            Say($"regex search: {Tally()}");
            Check("a regex search finds something", Tally().Contains("Match"), Tally());

            // ...and one that cannot match must say so, or the regex is not really being used.
            _app.SetText(edit, "HCI_RegUpdate[0-9]{6}");
            Thread.Sleep(400);
            Keyboard.Press(VirtualKeyShort.RETURN);
            Thread.Sleep(6000);
            Say($"impossible regex: {Tally()}");
            Check("and one that cannot match says so", Tally() == "No matches", Tally());
            regex.IsChecked = false;
        }
        Shot("backwards");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(600);
        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(600);
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
        var dlg = _app.FindDialog("Find");
        if (dlg is null) { Check("the bar opened", false); return; }
        var edit = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!;
        _app.SetText(edit, "");
        Thread.Sleep(200);
        Keyboard.Type("HCI_RegUpdate");
        Thread.Sleep(1500);
        int typed = MarkedPixels();
        Say($"marked pixels: {plain} -> {typed} on typing alone");
        Check("typing alone marks what is on screen", typed > plain + 500, $"{plain} -> {typed}");
        Shot("highlight-typed");

        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(2500);
        int found = MarkedPixels(current: true);
        Check("the line the search landed on is marked more strongly", found > 30, $"{found} strong pixels");
        Shot("highlight-found");

        Keyboard.Press(VirtualKeyShort.ESCAPE);   // close the bar
        Thread.Sleep(600);
        Check("the marks outlive the bar", MarkedPixels() > 500, $"{MarkedPixels()} marked pixels");

        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        Keyboard.Press(VirtualKeyShort.ESCAPE);   // drop the term
        Thread.Sleep(800);
        int cleared = MarkedPixels();
        Check("and Esc takes them away", cleared < 200, $"{cleared} marked pixels");
        Shot("highlight-cleared");
    }

    private void ColumnsAndWrap()
    {
        // Word wrap and columns cannot both be on; the menu has to say so rather than quietly ignore it.
        var view = OpenMenu("View");
        var wrap = view?.FirstOrDefault(m => (m.Name ?? "") == "Word Wrap");
        Check("Word Wrap is offered", wrap is not null, string.Join("|", view?.Select(m => m.Name) ?? Array.Empty<string>()));
        Check("and is available while there are no columns", wrap?.IsEnabled ?? false);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
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

    /// <summary>The saved filter set matches nothing in this file, so first make the view show something.</summary>
    private void GetContentOnScreen()
    {
        Check("the file is indexed", Status().Contains("Total: 33,180,857"), Status());

        var node = _app.FilterNode("[order-service]");
        Check("the [order-service] filter is in the list", node is not null);
        if (node is null) return;

        node.AsTreeItem().Select();
        Thread.Sleep(300);
        _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);   // the subtree, so plenty matches
        WaitFiltered();
        Say($"after enabling [order-service]: {Status()}");
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
            Check("the tip names a filter that matched", text.Contains("order-service", StringComparison.OrdinalIgnoreCase), text);
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

        Mouse.MoveTo(new Point(r.Left + 260, y));
        Thread.Sleep(400);
        Mouse.DoubleClick(MouseButton.Left);
        Thread.Sleep(600);
        Shot("selection-double");
        string word = CopyToClipboard();
        Say($"double-click copy: '{Trim(word)}'");
        Check("double-click takes a word, not the line",
              word.Length > 0 && !word.Contains('\n') && !word.Contains(' ') && word.Length < 60, $"'{Trim(word)}'");

        Mouse.MoveTo(new Point(r.Left + 260, y));
        Thread.Sleep(400);
        Mouse.DoubleClick(MouseButton.Left);
        Thread.Sleep(80);
        Mouse.Click(MouseButton.Left);
        Thread.Sleep(600);
        Shot("selection-triple");
        string line = CopyToClipboard();
        Say($"triple-click copy: {line.Length} chars '{Trim(line)}'");
        Check("triple-click takes the whole line", line.Length > word.Length, $"{line.Length} vs {word.Length}");
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
        string at = _app.StatusText("Ln:");
        for (int i = 0; i < 3; i++) { Keyboard.Press(VirtualKeyShort.DOWN); Thread.Sleep(150); }
        Check("the caret still moves while wrapped", _app.StatusText("Ln:") != at, $"{at} -> {_app.StatusText("Ln:")}");

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
                  _app.StatusText("Ln:").StartsWith($"Ln: {want} "), $"wanted {want}, got {_app.StatusText("Ln:")}");
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
        bool scrolled = _app.ScrollVerticalTo(150_000);
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

            if (ClickFilterRow("[order-service]"))
            {
                _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
                WaitFiltered();
                _app.ScrollVerticalTo(150_000);
                Thread.Sleep(1500);
                Check("and coloured again when one is turned back on", MapColours(map).Count > 0,
                      string.Join(" ", MapColours(map)));
                Shot("map-two-filters");
            }

            // The colours have to be the filters' own, on the real thing and not just in a fixture.
            foreach (string name in new[] { "[order-service]", "[OrderDispatchLoop]" })
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
            _app.ScrollVerticalTo(1_000_000);
            Thread.Sleep(900);
            long highLine = _app.FirstVisibleLine();
            using var atHigh = Grab(map);
            _app.ScrollVerticalTo(8_000_000);
            Thread.Sleep(900);
            long lowLine = _app.FirstVisibleLine();
            using var atLow = Grab(map);
            Say($"map across a 7-million-row jump ({highLine} -> {lowLine}): " +
                $"{PictureDiff(atHigh, atLow):P0} of the pixels changed");
            Check("the map shows somewhere else entirely after a long scroll", PictureDiff(atHigh, atLow) > 0.10,
                  $"{PictureDiff(atHigh, atLow):P0} of the pixels changed, view {highLine} -> {lowLine}");

            // Clicking the map moves the view without the scrollbar going anywhere much: it is the fine
            // adjustment, and the file is far too long for a window of it to register on the whole scale.
            _app.ScrollVerticalTo(15_000_000);
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
                _app.ScrollVerticalTo(8_300_000);
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
        var dlg = _app.FindDialog("Find");
        Check("Ctrl+F opens the bar", dlg is not null);
        if (dlg is null) return;
        var edit = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!;
        Check("with the keyboard in its box", Focused(edit), FocusedName());

        long top = _app.FirstVisibleLine();
        _app.SetText(edit, "");
        Thread.Sleep(200);
        Keyboard.Type("hci_regupdate");
        Thread.Sleep(1500);
        Check("typing does not move the view", _app.FirstVisibleLine() == top, $"{top} -> {_app.FirstVisibleLine()}");
        Shot("find-typing");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(300);
        while ((Tally().Length == 0 || Tally().Contains('+') || Tally() == "Searching\u2026") && sw.ElapsedMilliseconds < 30000)
            Thread.Sleep(100);
        Say($"first search + full sweep: {sw.ElapsedMilliseconds} ms -> {Tally()}");
        Check("the counts settle without a plus", !Tally().Contains('+'), Tally());
        Check("and say where we are", Tally().StartsWith("Match "), Tally());
        Shot("find-found");

        string at = _app.StatusText("Ln:");
        for (int i = 0; i < 10; i++) { Keyboard.Press(VirtualKeyShort.RETURN); Thread.Sleep(70); }
        Thread.Sleep(1000);
        Say($"after ten repeats: {_app.StatusText("Ln:")} (was {at}), {Tally()}");
        Check("ten repeats moved the caret on", _app.StatusText("Ln:") != at, $"{at} -> {_app.StatusText("Ln:")}");

        // Ctrl+F while the box already has the keyboard: typing must replace the term.
        CtrlF();
        Thread.Sleep(300);
        Keyboard.Type("smpdib");
        Thread.Sleep(500);
        Check("Ctrl+F selects the term so a new one types straight over it", _app.TextOf(edit) == "smpdib", _app.TextOf(edit));

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
        Keyboard.Press(VirtualKeyShort.DOWN);
        Thread.Sleep(800);
        Say($"after Down in an empty box: '{_app.TextOf(edit)}'");
        Check("Down recalls the most recent term", _app.TextOf(edit).Length > 0, _app.TextOf(edit));
        ShotScreen("find-history");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);

        // Esc chain.
        _app.SetText(edit, "hci_regupdate");
        Thread.Sleep(200);
        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(3000);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(600);
        Check("Esc closes the bar", _app.FindDialog("Find") is null or { IsOffscreen: true });
        Check("but the counts stay", Tally().Length > 0, Tally());

        _app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        for (int i = 0; i < 4; i++) { Keyboard.Press(VirtualKeyShort.DOWN); Thread.Sleep(250); }
        string moved = Tally();
        Say($"counts after arrowing off a match: {moved}");
        Check("the counts never read as a bare number",
              moved.Length > 0 && !long.TryParse(moved.Replace(",", ""), out _), moved);
        Shot("find-after-arrows");

        // Hiding and showing must move the split. The date is on every line, so half the hits are on lines
        // the [order-service] filter is not showing.
        CtrlF();
        Thread.Sleep(400);
        var box = _app.FindDialog("Find")?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (box is not null)
        {
            _app.SetText(box, "2026-07-16T18");
            Thread.Sleep(300);
            Keyboard.Press(VirtualKeyShort.RETURN);
            Thread.Sleep(6000);
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(500);
        }
        string dim = Tally();
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

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(800);
        Check("Esc again puts the term away", Tally().Length == 0, Tally());
        Shot("find-cleared");
    }

    private void UndoRedo()
    {
        if (!ClickFilterRow("[order-service]")) { Check("a filter to work on", false); return; }
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
                          .FirstOrDefault(t => (t.Name ?? "").StartsWith("No presets yet"));
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
        if (save is null) { Keyboard.Press(VirtualKeyShort.ESCAPE); return; }

        // Chosen with the keyboard: it opens a modal dialog, and a UIA Invoke that does that never returns.
        Keyboard.Press(VirtualKeyShort.DOWN);
        Thread.Sleep(200);
        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(1500);
        ShotScreen("presets-naming");
        Keyboard.Type("order-service only");
        Thread.Sleep(400);
        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(1800);
        Say($"presets now: {string.Join(" | ", SafePresetNames())}");
        Check("the preset appears in the list", SafePresetNames().Any(n => n.Contains("order-service")),
              string.Join("|", SafePresetNames()));
        Shot("presets");

        // Turning its filters off must clear it; clicking it must bring them back.
        if (ClickFilterRow("[order-service]"))
        {
            _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
            Thread.Sleep(4000);
            Say($"after switching [order-service] off: {Status()}");
            Say($"still ticked: {string.Join(" | ", TickedFilters())}");
            Check("switching its filters off drops the preset out of effect",
                  !_app.ActivePresets().Any(n => n.Contains("order-service")), string.Join("|", _app.ActivePresets()));

            _app.SelectPreset("order-service only");
            Thread.Sleep(5000);
            Check("selecting it turns them back on", _app.ActivePresets().Any(n => n.Contains("order-service")),
                  string.Join("|", _app.ActivePresets()));
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

    private string Tally()
    {
        foreach (var t in _app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
        {
            string n = t.Name ?? "";
            if (n.StartsWith("Match ") || n.EndsWith(" matches") || n.EndsWith(" lines") ||
                n.Contains(" hidden") || n == "No matches" || n == "Searching\u2026") return n;
        }
        return "";
    }

    private void WaitIndexed()
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until && !Status().Contains("Total: 33,180,857")) Thread.Sleep(500);
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

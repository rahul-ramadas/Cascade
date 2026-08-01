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
        void Stage(string name, Action run)
        {
            if (only.Length > 0 && name != "content" && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return;
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

    /// <summary>Markers draw down the map's left edge, so setting one has to change what it paints.</summary>
    private void MarkersAndMap()
    {
        ClickRow(20);
        Thread.Sleep(400);
        var map = _app.Grid().FindAllChildren(cf => cf.ByControlType(ControlType.ScrollBar))
                      .FirstOrDefault(s => (s.Name ?? "") == "Match map");
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
        var nr = node.BoundingRectangle;
        Mouse.Click(new Point(nr.Left + 40, nr.Top + nr.Height / 2));
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

        Mouse.Click(new Point(nr.Left + 40, nr.Top + nr.Height / 2));
        Thread.Sleep(400);
        Chord(VirtualKeyShort.KEY_Z);
        Thread.Sleep(2000);
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
        Check("the map stands in for the scrollbar", _app.VerticalScrollerName() == "Match map", _app.VerticalScrollerName());
        long first = _app.FirstVisibleLine();
        bool scrolled = _app.ScrollVerticalTo(150_000);
        Check("the map scrolls the view", scrolled && _app.FirstVisibleLine() != first,
              $"{first} -> {_app.FirstVisibleLine()}");
        Shot("map");

        _app.ClickMenuOrThrow("View", "Show Match Map");
        Thread.Sleep(800);
        Check("turning it off brings the scrollbar back", _app.VerticalScrollerName() != "Match map", _app.VerticalScrollerName());
        _app.ClickMenuOrThrow("View", "Show Match Map");
        Thread.Sleep(800);
        Check("and back on returns the map", _app.VerticalScrollerName() == "Match map", _app.VerticalScrollerName());
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
        var node = _app.FilterNode("[order-service]");
        if (node is null) { Check("a filter to work on", false); return; }
        var nr = node.BoundingRectangle;
        Mouse.Click(new Point(nr.Left + 40, nr.Top + nr.Height / 2));   // real click, so the tree has focus
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
        var node = _app.FilterNode("[order-service]");
        if (node is not null)
        {
            var nr = node.BoundingRectangle;
            Mouse.Click(new Point(nr.Left + 40, nr.Top + nr.Height / 2));
            Thread.Sleep(400);
            _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
            Thread.Sleep(4000);
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

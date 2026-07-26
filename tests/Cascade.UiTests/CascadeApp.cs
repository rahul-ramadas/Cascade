using System.Diagnostics;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Application = FlaUI.Core.Application; // disambiguate from System.Windows.Forms.Application

namespace Cascade.UiTests;

/// <summary>
/// Launches the real Cascade.exe on a deterministic test file and drives it through Windows UI
/// Automation (FlaUI). Exposes helpers to read the status bar, inspect the log grid's accessible
/// rows (selection + on-screen bounds), send shortcuts, and navigate menus.
/// </summary>
internal sealed class CascadeApp : IDisposable
{
    private readonly Application _app;
    private readonly UIA3Automation _automation;
    private readonly string _logFile;
    private readonly string _filterFile;
    private readonly string _settingsDir;

    public Window Window { get; }

    private CascadeApp(Application app, UIA3Automation automation, Window window, string logFile, string filterFile, string settingsDir)
    {
        _app = app;
        _automation = automation;
        Window = window;
        _logFile = logFile;
        _filterFile = filterFile;
        _settingsDir = settingsDir;
    }

    public static CascadeApp Launch()
        => LaunchExisting(TestData.WriteLogFile(), TestData.WriteFilterFile(), NewSettingsDir(),
               ownsFiles: true, ownsSettingsDir: true);

    /// <summary>Returns a fresh, unique settings directory path (not yet created on disk).</summary>
    public static string NewSettingsDir()
        => Path.Combine(Path.GetTempPath(), "cascade_uitest_cfg_" + Guid.NewGuid().ToString("N"));

    /// <summary>Launches Cascade on an explicit log file and (optional) filter file, pointing the app at
    /// an isolated settings directory (via <c>CASCADE_SETTINGS_DIR</c>) so tests never read or write the
    /// user's real config. When <paramref name="ownsFiles"/> is false the caller owns
    /// <paramref name="log"/>/<paramref name="tat"/> (needed for multi-launch scenarios such as auto-load,
    /// where the same files must survive across two app instances).</summary>
    public static CascadeApp LaunchExisting(string log, string? tat, string settingsDir, bool ownsFiles, bool ownsSettingsDir)
    {
        string exe = TestData.AppExe();
        string args = tat is null ? $"\"{log}\"" : $"\"{log}\" /Filters:\"{tat}\"";
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false };
        psi.EnvironmentVariables["CASCADE_SETTINGS_DIR"] = settingsDir;
        var app = Application.Launch(psi);
        var automation = new UIA3Automation();
        var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20))
                     ?? throw new InvalidOperationException("Main window did not appear.");
        var harness = new CascadeApp(app, automation, window,
            ownsFiles ? log : "", ownsFiles ? (tat ?? "") : "", ownsSettingsDir ? settingsDir : "");
        harness.Activate();
        // Wait for indexing to finish (Total shows the full line count).
        harness.WaitStatus("Total:", $"Total: {TestData.LineCount:N0}", 20000);
        return harness;
    }

    public void Activate()
    {
        try { Window.SetForeground(); } catch { /* best effort */ }
        System.Threading.Thread.Sleep(50);
    }

    // ---- status bar ----

    /// <summary>Text of the first status-bar label whose text starts with <paramref name="prefix"/>.</summary>
    public string StatusText(string prefix)
    {
        foreach (var label in Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
        {
            string n = label.Name ?? "";
            if (n.StartsWith(prefix, StringComparison.Ordinal)) return n;
        }
        return "";
    }

    public bool WaitStatus(string prefix, string expected, int ms = 8000)
        => Retry.WhileFalse(() => StatusText(prefix) == expected, TimeSpan.FromMilliseconds(ms),
               TimeSpan.FromMilliseconds(50)).Result;

    // ---- log grid accessibility ----

    public AutomationElement Grid()
        => Retry.WhileNull(() => Window.FindFirstDescendant(cf => cf.ByName("Cascade log view")),
               TimeSpan.FromSeconds(5)).Result ?? throw new InvalidOperationException("Log grid element not found.");

    public AutomationElement[] Rows()
        => Grid().FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));

    /// <summary>The 1-based file line and on-screen bounds of the selected row (or null if none visible).</summary>
    public (int line, Rectangle bounds)? SelectedRow()
    {
        foreach (var r in Rows())
        {
            var la = r.Patterns.LegacyIAccessible.PatternOrDefault;
            if (la is null) continue;
            var state = la.State.ValueOrDefault;
            if (state.HasFlag(AccessibilityState.STATE_SYSTEM_SELECTED))
            {
                _ = int.TryParse(la.Value.ValueOrDefault, out int line);
                return (line, r.BoundingRectangle);
            }
        }
        return null;
    }

    /// <summary>True if the selected row is vertically centered in the grid (within a few rows).</summary>
    public bool SelectedRowIsCentered(out string detail)
    {
        detail = "";
        var sel = SelectedRow();
        if (sel is null) { detail = "no selected row visible"; return false; }
        Rectangle grid = Grid().BoundingRectangle;
        Rectangle row = sel.Value.bounds;
        int gridCenter = grid.Top + grid.Height / 2;
        int rowCenter = row.Top + row.Height / 2;
        int tolerance = Math.Max(row.Height * 4, 12);
        detail = $"line={sel.Value.line} rowCenterY={rowCenter} gridCenterY={gridCenter} tol={tolerance}";
        return Math.Abs(rowCenter - gridCenter) <= tolerance;
    }

    // ---- filter tree ----

    public AutomationElement Tree()
        => Retry.WhileNull(() => Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree)),
               TimeSpan.FromSeconds(5)).Result ?? throw new InvalidOperationException("Filter tree not found.");

    public AutomationElement? FilterNode(string containsText)
        => Tree().FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                 .FirstOrDefault(n => (n.Name ?? "").Contains(containsText, StringComparison.OrdinalIgnoreCase));

    // ---- actions via menus / dialogs ----

    /// <summary>Opens Find (via the Edit menu), searches forward for <paramref name="text"/>, then closes it.</summary>
    public void FindText(string text)
    {
        ClickMenu("Edit", "Find");
        var dlg = FindDialog("Find") ?? throw new InvalidOperationException("Find dialog did not open.");
        var edit = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        var vp = edit?.Patterns.Value.PatternOrDefault;
        if (vp is not null && !vp.IsReadOnly.ValueOrDefault) vp.SetValue(text);
        dlg.FindFirstDescendant(cf => cf.ByName("Find Next"))?.AsButton().Invoke();
        System.Threading.Thread.Sleep(150);
        try { dlg.Close(); } catch { /* modeless: hides */ }
    }

    public void ToggleFilteredMode() => ClickMenu("View", "Show Only Filtered Lines");

    /// <summary>File -&gt; Close Filters: clears filters, detaches the filter file, and stops auto-load.</summary>
    public void CloseFilters() => ClickMenu("File", "Close Filters");
    public void FindNextForSelectedFilter() => ClickMenu("Filters", "Find Next Match");
    public void FindPrevForSelectedFilter() => ClickMenu("Filters", "Find Previous Match");

    /// <summary>Opens the (modeless) Find dialog via the Edit menu and returns its window.</summary>
    public Window OpenFindDialog()
    {
        ClickMenu("Edit", "Find");
        return FindDialog("Find") ?? throw new InvalidOperationException("Find dialog did not open.");
    }

    /// <summary>Types <paramref name="text"/> into an open Find dialog and clicks Find Next/Previous.</summary>
    public void FindInDialog(Window dlg, string text, bool forward)
    {
        var edit = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        var vp = edit?.Patterns.Value.PatternOrDefault;
        if (vp is not null && !vp.IsReadOnly.ValueOrDefault) vp.SetValue(text);
        dlg.FindFirstDescendant(cf => cf.ByName(forward ? "Find Next" : "Find Previous"))?.AsButton().Invoke();
        System.Threading.Thread.Sleep(200);
    }

    /// <summary>The concatenated text labels in a dialog (used to read the Find status message).</summary>
    public string DialogText(Window dlg)
        => string.Join(" | ", dlg.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name ?? "").Where(n => n.Length > 0));

    /// <summary>Waits until a dialog's text labels contain <paramref name="substring"/> (find runs async).</summary>
    public bool WaitDialogText(Window dlg, string substring, int ms = 8000)
        => Retry.WhileFalse(() => DialogText(dlg).Contains(substring, StringComparison.OrdinalIgnoreCase),
               TimeSpan.FromMilliseconds(ms), TimeSpan.FromMilliseconds(50)).Result;

    /// <summary>Text of the currently selected log row (its full line), or "" if none selected/visible.</summary>
    public string SelectedRowText()
    {
        foreach (var r in Rows())
        {
            var la = r.Patterns.LegacyIAccessible.PatternOrDefault;
            if (la is not null && la.State.ValueOrDefault.HasFlag(AccessibilityState.STATE_SYSTEM_SELECTED))
                return r.Name ?? "";
        }
        return "";
    }

    /// <summary>Waits until the selected log row's text contains <paramref name="substring"/> (find runs async).</summary>
    public bool WaitSelectedRowText(string substring, int ms = 8000)
        => Retry.WhileFalse(() => SelectedRowText().Contains(substring, StringComparison.Ordinal),
               TimeSpan.FromMilliseconds(ms), TimeSpan.FromMilliseconds(50)).Result;

    /// <summary>Reads the system clipboard as text (on an STA thread, since the app set it cross-process).</summary>
    public static string ReadClipboardText()
    {
        string text = "";
        var t = new Thread(() => { try { text = System.Windows.Forms.Clipboard.GetText(); } catch { /* empty */ } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(2000);
        return text;
    }

    public Window? FindDialog(string title)
        => Retry.WhileNull(() =>
               (_automation.GetDesktop().FindFirstChild(cf => cf.ByName(title))
                ?? _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(title)))?.AsWindow(),
               TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)).Result;

    // ---- menus ----

    private AutomationElement MenuBar()
        => Retry.WhileNull(() => Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar)),
               TimeSpan.FromSeconds(5)).Result ?? throw new InvalidOperationException("Menu bar not found.");

    private AutomationElement[] TopItems()
        => MenuBar().FindAllChildren(cf => cf.ByControlType(ControlType.MenuItem));

    /// <summary>Top-level menu names (e.g. File, Edit, View, Filters, Help).</summary>
    public string[] TopMenuNames() => TopItems().Select(m => m.Name ?? "").ToArray();

    /// <summary>Opens a top menu and returns its item names (WinForms dropdowns only exist once open).</summary>
    public string[] MenuItemNames(string topMenu)
    {
        var top = TopItems().FirstOrDefault(m => Norm(m.Name) == Norm(topMenu));
        if (top is null) return Array.Empty<string>();
        Expand(top);
        var names = OpenDropDownItems().Select(m => m.Name ?? "").Where(n => n.Length > 0).ToArray();
        Collapse(top);
        return names;
    }

    /// <summary>Opens a menu path (ExpandCollapse to open, then Invoke the leaf). Works for non-modal
    /// and modeless items; do NOT use for modal-dialog items (their message loop blocks UIA Invoke).</summary>
    public bool ClickMenu(params string[] path)
    {
        var top = TopItems().FirstOrDefault(m => Norm(m.Name) == Norm(path[0]));
        if (top is null) return false;
        Expand(top);
        for (int i = 1; i < path.Length; i++)
        {
            var item = FindOpenMenuItem(path[i]);
            if (item is null) { Collapse(top); return false; }
            if (i < path.Length - 1) Expand(item);
            else Invoke(item);
        }
        System.Threading.Thread.Sleep(150);
        return true;
    }

    /// <summary>Selects the given 1-based file line via the grid's accessibility (no dialog/foreground).</summary>
    public void SelectLine(int oneBasedLine)
    {
        var row = Retry.WhileNull(() =>
            Rows().FirstOrDefault(r => r.Patterns.LegacyIAccessible.PatternOrDefault?.Value.ValueOrDefault == oneBasedLine.ToString()),
            TimeSpan.FromSeconds(3)).Result ?? throw new InvalidOperationException($"Row for line {oneBasedLine} not visible.");
        var la = row.Patterns.LegacyIAccessible.Pattern;
        la.Select(3); // SELFLAG_TAKEFOCUS | SELFLAG_TAKESELECTION
        System.Threading.Thread.Sleep(120);
    }

    /// <summary>Scrolls the log vertically by driving the grid's vertical scrollbar (UIA RangeValue),
    /// so an off-screen line can be brought into view. <paramref name="firstRow"/> is the display row
    /// to put at the top. Returns false if the scrollbar doesn't expose a settable value.</summary>
    public bool ScrollVerticalTo(int firstRow)
    {
        var vbar = Grid().FindAllChildren(cf => cf.ByControlType(ControlType.ScrollBar))
                         .FirstOrDefault(s => s.BoundingRectangle.Height >= s.BoundingRectangle.Width);
        var rv = vbar?.Patterns.RangeValue.PatternOrDefault;
        if (rv is null || rv.IsReadOnly.ValueOrDefault) return false;
        rv.SetValue(firstRow);
        System.Threading.Thread.Sleep(150);
        return true;
    }

    private static void Expand(AutomationElement item)
    {
        var ec = item.Patterns.ExpandCollapse.PatternOrDefault;
        if (ec is not null) { try { ec.Expand(); } catch { /* fall through */ } }
        else item.Patterns.Invoke.PatternOrDefault?.Invoke();
        System.Threading.Thread.Sleep(150);
    }

    private static void Collapse(AutomationElement item)
    {
        try { item.Patterns.ExpandCollapse.PatternOrDefault?.Collapse(); } catch { /* ignore */ }
    }

    private static void Invoke(AutomationElement item)
    {
        var inv = item.Patterns.Invoke.PatternOrDefault;
        if (inv is not null) inv.Invoke(); else item.Click();
    }

    /// <summary>Menu items currently present in the window subtree (top-level items plus any open
    /// dropdown's items — WinForms surfaces the opened dropdown inside the window's UIA tree).</summary>
    private AutomationElement[] OpenDropDownItems()
        => Window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem));

    private AutomationElement? FindOpenMenuItem(string name)
        => Retry.WhileNull(() =>
               OpenDropDownItems().FirstOrDefault(m => Norm(m.Name) == Norm(name))
               ?? OpenDropDownItems().FirstOrDefault(m => Norm(m.Name).StartsWith(Norm(name), StringComparison.OrdinalIgnoreCase)),
               TimeSpan.FromSeconds(2)).Result;

    private static string Norm(string? s) => (s ?? "").Replace("&", "").Split('\t')[0].Replace("\u2026", "").Trim();

    public void Dispose()
    {
        try { _app.Kill(); } catch { /* ignore */ }
        _automation.Dispose();
        try { if (_logFile.Length > 0) File.Delete(_logFile); } catch { /* ignore */ }
        try { if (_filterFile.Length > 0) File.Delete(_filterFile); } catch { /* ignore */ }
        try { if (_settingsDir.Length > 0 && Directory.Exists(_settingsDir)) Directory.Delete(_settingsDir, true); } catch { /* ignore */ }
    }
}

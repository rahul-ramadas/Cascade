using System.Diagnostics;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
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
    public static CascadeApp LaunchExisting(string log, string? tat, string settingsDir, bool ownsFiles, bool ownsSettingsDir,
                                            IDictionary<string, string>? environment = null, string? exePath = null)
    {
        string exe = exePath ?? TestData.AppExe();
        string args = tat is null ? $"\"{log}\"" : $"\"{log}\" /Filters:\"{tat}\"";
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false };
        psi.EnvironmentVariables["CASCADE_SETTINGS_DIR"] = settingsDir;
        // No test may reach out to GitHub: it would be slow, flaky offline, and could actually replace the
        // executable under test. The update tests opt back in explicitly, against a local stub server.
        psi.EnvironmentVariables["CASCADE_UPDATE"] = "off";
        if (environment is not null)
            foreach (var kv in environment) psi.EnvironmentVariables[kv.Key] = kv.Value;
        var app = Application.Launch(psi);
        var automation = new UIA3Automation();
        var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20))
                     ?? throw new InvalidOperationException("Main window did not appear.");
        var harness = new CascadeApp(app, automation, window,
            ownsFiles ? log : "", ownsFiles ? (tat ?? "") : "", ownsSettingsDir ? settingsDir : "");
        // Deliberately NOT brought to the foreground: everything here drives the app through automation
        // patterns or messages sent straight to its windows, so a run never takes focus from the user.
        // Wait for indexing to finish (Total shows the full line count).
        harness.WaitStatus("Total:", $"Total: {TestData.LineCount:N0}", 20000);
        return harness;
    }

    /// <summary>Brings the window to the front. Only for debugging a run by eye - the tests do not need it.</summary>
    public void Activate()
    {
        try { Window.SetForeground(); } catch { /* best effort */ }
        System.Threading.Thread.Sleep(50);
    }

    // ---- status bar ----

    private AutomationElement? _statusBar;

    /// <summary>The label-bearing elements of the status bar and the menu bar (the update notice lives
    /// there). Deliberately not a whole-window search: the window subtree contains every visible log row,
    /// so walking it to read one label costs about a second on a large screen - and WaitStatus polls it.
    /// The hosts are looked up until found, so this also copes with being called during startup.</summary>
    private IEnumerable<AutomationElement> LabelElements()
    {
        _statusBar ??= Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.StatusBar));
        foreach (var host in new[] { _statusBar, MenuBarOrNull() })
            if (host is not null)
                foreach (var label in host.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
                    yield return label;
    }

    /// <summary>Text of the first status-bar label whose text starts with <paramref name="prefix"/>.</summary>
    public string StatusText(string prefix)
    {
        foreach (var label in LabelElements())
        {
            string n = label.Name ?? "";
            if (n.StartsWith(prefix, StringComparison.Ordinal)) return n;
        }
        return "";
    }

    public bool WaitStatus(string prefix, string expected, int ms = 8000)
        => Retry.WhileFalse(() => StatusText(prefix) == expected, TimeSpan.FromMilliseconds(ms),
               Poll).Result;

    /// <summary>Waits for any status label to start with <paramref name="prefix"/>, returning its full text.</summary>
    public string WaitForStatus(string prefix, int ms = 30000)
        => Retry.WhileEmpty(() => StatusText(prefix), TimeSpan.FromMilliseconds(ms),
               TimeSpan.FromMilliseconds(100)).Result ?? "";

    /// <summary>Every element carrying text, for when an expected label cannot be found. Log rows are left
    /// out: there are hundreds and they drown everything else.</summary>
    public string DescribeTextElements()
    {
        var parts = Window.FindAllDescendants()
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.ControlType != ControlType.ListItem)
            .Select(e => $"{e.ControlType}:'{e.Name}'")
            .Take(60);
        return string.Join(", ", parts);
    }

    /// <summary>The first non-row element whose name starts with <paramref name="prefix"/>, or null.</summary>
    public AutomationElement? Element(string prefix)
        => Window.FindAllDescendants()
                 .FirstOrDefault(e => e.ControlType != ControlType.ListItem &&
                                      (e.Name ?? "").StartsWith(prefix, StringComparison.Ordinal));

    // The app repaints on a 33ms timer, so anything driven through UIA lands within a frame or two of the
    // call returning. Waiting for the effect itself is always better and is what most helpers below do;
    // Settle is for the few places where the effect is not observable through automation. Keep it in
    // frames rather than round numbers so it stays tied to the reason it exists.
    private const int FrameMs = 33;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);
    private static void Settle(int frames = 2) => System.Threading.Thread.Sleep(FrameMs * frames);

    /// <summary>Un-maximizes and resizes the window, to check layout when space runs short. Waits for the
    /// window to actually move: a resize that silently did nothing makes every later assertion pass
    /// vacuously, which is a miserable thing to debug. A request below the form's MinimumSize legitimately
    /// settles at that floor, so the achieved size is returned rather than demanded.</summary>
    public Size ResizeTo(int width, int height)
    {
        var before = Window.BoundingRectangle.Size;
        Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        Window.Patterns.Transform.Pattern.Resize(width, height);
        if (!Retry.WhileFalse(() => Window.BoundingRectangle.Size != before,
                              TimeSpan.FromSeconds(5), Poll).Result)
            throw new InvalidOperationException(
                $"Window ignored the resize to {width}x{height}; it is still {before.Width}x{before.Height}.");
        Settle();   // let the relayout land before anything is measured
        return Window.BoundingRectangle.Size;
    }

    /// <summary>
    /// Closes the window and waits for the process to actually exit. <see cref="Dispose"/> kills the
    /// process, which skips everything the app does on the way out - including installing an update.
    /// </summary>
    public bool CloseGracefully(int ms = 30000)
    {
        try { Window.Close(); } catch { /* it may already be going */ }
        try
        {
            using var p = Process.GetProcessById(_app.ProcessId);
            return p.WaitForExit(ms);
        }
        catch { return true; }   // already gone
    }

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

    /// <summary>Names of the top-level filters, in list order.</summary>
    public string[] RootFilterNames()
        => Tree().FindAllChildren(cf => cf.ByControlType(ControlType.TreeItem)).Select(n => n.Name ?? "").ToArray();

    /// <summary>Names of the filters nested directly under the named one, in list order.</summary>
    public string[] ChildFilterNames(string parentContains)
    {
        var parent = FilterNode(parentContains);
        if (parent is null) return Array.Empty<string>();
        return parent.FindAllChildren(cf => cf.ByControlType(ControlType.TreeItem)).Select(n => n.Name ?? "").ToArray();
    }

    /// <summary>Position of a filter within <paramref name="names"/>, or -1.</summary>
    public static int IndexOfFilter(string[] names, string containsText)
        => Array.FindIndex(names, n => n.Contains(containsText, StringComparison.OrdinalIgnoreCase));

    /// <summary>The lowest 1-based file line currently on screen, or -1 when nothing is visible.</summary>
    public int FirstVisibleLine()
    {
        var lines = Rows()
            .Select(r => int.TryParse(r.Patterns.LegacyIAccessible.PatternOrDefault?.Value.ValueOrDefault, out int n) ? n : -1)
            .Where(n => n > 0).ToArray();
        return lines.Length == 0 ? -1 : lines.Min();
    }

    /// <summary>Selects a filter and gives the list keyboard focus, so shortcuts are routed to it.</summary>
    public void FocusFilter(string containsText)
    {
        var node = FilterNode(containsText)
            ?? throw new InvalidOperationException($"Filter '{containsText}' not in the list.");
        node.AsTreeItem().Select();
        Retry.WhileFalse(() => node.Patterns.LegacyIAccessible.PatternOrDefault?.State.ValueOrDefault
                                   .HasFlag(AccessibilityState.STATE_SYSTEM_SELECTED) == true,
                         TimeSpan.FromSeconds(2), Poll);
        Settle(1);
    }

    /// <summary>Waits for a status-bar message containing <paramref name="containsText"/>. The whole-window
    /// flash is far too brief to observe, so the wording in the status bar is what the tests assert on.</summary>
    public bool WaitForFindMessage(string containsText, int ms = 4000)
        => Retry.WhileFalse(
               () => Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                           .Any(t => (t.Name ?? "").Contains(containsText, StringComparison.OrdinalIgnoreCase)),
               TimeSpan.FromMilliseconds(ms), TimeSpan.FromMilliseconds(40)).Result;

    // ---- keyboard that does not need the foreground ----

    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int KeyDownLParam = 0x0000_0001;          // repeat count 1
    private const int KeyUpLParam = unchecked((int)0xC000_0001); // repeat 1, previously down, transition up

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    /// <summary>
    /// Gives a control the application's keyboard focus without taking the desktop's foreground away from
    /// whatever the user is doing. Attaching our input queue to the application's makes SetFocus apply to its
    /// queue; WinForms then routes posted key messages exactly as it would for a real keypress.
    /// </summary>
    private static void GiveKeyboardFocus(IntPtr hwnd)
    {
        uint target = GetWindowThreadProcessId(hwnd, out _);
        uint self = GetCurrentThreadId();
        if (target == 0) return;
        if (target == self) { SetFocus(hwnd); return; }

        AttachThreadInput(self, target, true);
        try { SetFocus(hwnd); }
        finally { AttachThreadInput(self, target, false); }
    }

    /// <summary>
    /// Delivers a keystroke straight to <paramref name="target"/>'s window procedure, which is how a control
    /// receives one it handles in KeyDown. Nothing here depends on the foreground or on which window the
    /// application considers active, so a test using it keeps working whatever else is going on - unlike
    /// synthesised global input, which simply goes wherever the foreground happens to be.
    /// <para>
    /// Modifiers are folded into wParam rather than pressed separately: injected key messages do not update
    /// the thread's key state, so GetKeyState - and therefore WinForms' ModifierKeys - would still report
    /// nothing held. WinForms builds its key data as <c>(Keys)wParam | ModifierKeys</c>, so setting the
    /// modifier bits in wParam produces exactly the KeyEventArgs a real keypress would.
    /// </para>
    /// </summary>
    public void SendKey(AutomationElement target, VirtualKeyShort key, params VirtualKeyShort[] modifiers)
        => DeliverKey(target, key, modifiers, viaMessageLoop: false);

    /// <summary>
    /// Same, but routed through the message loop so the pre-processing that drives ProcessCmdKey, dialog keys
    /// and access keys runs. Needed for shortcuts handled at form level rather than in a control's KeyDown -
    /// but it only arrives while the target's window is the one the application considers active.
    /// </summary>
    public void SendKeyAsDialogKey(AutomationElement target, VirtualKeyShort key, params VirtualKeyShort[] modifiers)
        => DeliverKey(target, key, modifiers, viaMessageLoop: true);

    private void DeliverKey(AutomationElement target, VirtualKeyShort key, VirtualKeyShort[] modifiers, bool viaMessageLoop)
    {
        IntPtr hwnd = target.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"'{target.Name}' has no window to send keys to.");
        GiveKeyboardFocus(hwnd);

        int wParam = (int)key;
        foreach (var m in modifiers)
            wParam |= m switch
            {
                VirtualKeyShort.CONTROL or VirtualKeyShort.LCONTROL or VirtualKeyShort.RCONTROL => 0x0002_0000, // Keys.Control
                VirtualKeyShort.SHIFT or VirtualKeyShort.LSHIFT or VirtualKeyShort.RSHIFT => 0x0001_0000,       // Keys.Shift
                VirtualKeyShort.ALT or VirtualKeyShort.LMENU or VirtualKeyShort.RMENU => 0x0004_0000,           // Keys.Alt
                _ => throw new ArgumentException($"unsupported modifier {m}")
            };

        if (viaMessageLoop)
        {
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)wParam, KeyDownLParam);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)wParam, KeyUpLParam);
        }
        else
        {
            SendMessage(hwnd, WM_KEYDOWN, (IntPtr)wParam, KeyDownLParam);
            SendMessage(hwnd, WM_KEYUP, (IntPtr)wParam, KeyUpLParam);
        }
        Settle();
    }

    public void CtrlKey(AutomationElement target, VirtualKeyShort key) => SendKey(target, key, VirtualKeyShort.CONTROL);
    public void Key(AutomationElement target, VirtualKeyShort key) => SendKey(target, key);
    public void ShiftKey(AutomationElement target, VirtualKeyShort key) => SendKey(target, key, VirtualKeyShort.SHIFT);

    /// <summary>Puts text into an edit control through its value pattern, which needs no focus at all.</summary>
    public void SetText(AutomationElement edit, string text)
    {
        var vp = edit.Patterns.Value.PatternOrDefault;
        if (vp is null || vp.IsReadOnly.ValueOrDefault) throw new InvalidOperationException("edit is not writable");
        vp.SetValue(text);
        Retry.WhileFalse(() => vp.Value.ValueOrDefault == text, TimeSpan.FromSeconds(2), Poll);
        Settle();
    }

    /// <summary>Current text of an edit control.</summary>
    public string TextOf(AutomationElement edit) => edit.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? "";

    /// <summary>Every non-empty status-bar field, for diagnosing a failed expectation.</summary>
    public string AllStatusText()
        => string.Join(" | ", Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                    .Select(t => t.Name ?? "").Where(n => n.Length > 0));

    /// <summary>The filter list's search box. Found by name rather than "the first edit", because a hidden
    /// Find dialog is still part of the window's tree and would be picked up instead.</summary>
    public AutomationElement FilterSearchBox()
        => Retry.WhileNull(() => Window.FindFirstDescendant(cf => cf.ByName("Filter search")),
               TimeSpan.FromSeconds(5)).Result
           ?? throw new InvalidOperationException("filter search box not found");

    // ---- actions via menus / dialogs ----

    /// <summary>Opens Find (via the Edit menu), searches forward for <paramref name="text"/>, then closes it.</summary>
    public void FindText(string text)
    {
        ClickMenuOrThrow("Edit", "Find");
        var dlg = FindDialog("Find") ?? throw new InvalidOperationException("Find dialog did not open.");
        var edit = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        var vp = edit?.Patterns.Value.PatternOrDefault;
        if (vp is not null && !vp.IsReadOnly.ValueOrDefault) vp.SetValue(text);
        dlg.FindFirstDescendant(cf => cf.ByName("Find Next"))?.AsButton().Invoke();
        Settle();
        try { dlg.Close(); } catch { /* modeless: hides */ }
    }

    public void ToggleFilteredMode()
    {
        bool expected = TryReadFilteredModeFromMenu() is { } current ? !current : !VisibleRowsLookFiltered();
        ClickMenuOrThrow("View", "Show Only Filtered Lines");
        WaitForFilteredMode(expected);
    }

    /// <summary>Sets filtered/dim mode to the requested state, verifying an observable end state.</summary>
    public void SetFilteredMode(bool expected)
    {
        bool? current = TryReadFilteredModeFromMenu();
        if (current == expected || (current is null && FilteredModeLooksLike(expected))) return;

        ClickMenuOrThrow("View", "Show Only Filtered Lines");
        WaitForFilteredMode(expected);
    }

    /// <summary>True when visible test rows all come from the imported MATCH filter.</summary>
    public bool VisibleRowsLookFiltered() => RowsAreOnlyMatchLines();

    /// <summary>File -&gt; Close Filters: clears filters, detaches the filter file, and stops auto-load.</summary>
    public void CloseFilters() => ClickMenuOrThrow("File", "Close Filters");
    public void FindNextForSelectedFilter() => ClickMenuOrThrow("Filters", "Find Next Match");
    public void FindPrevForSelectedFilter() => ClickMenuOrThrow("Filters", "Find Previous Match");

    /// <summary>Opens the (modeless) Find dialog via the Edit menu and returns its window.</summary>
    public Window OpenFindDialog()
    {
        ClickMenuOrThrow("Edit", "Find");
        return FindDialog("Find") ?? throw new InvalidOperationException("Find dialog did not open.");
    }

    /// <summary>Types <paramref name="text"/> into an open Find dialog and clicks Find Next/Previous.</summary>
    public void FindInDialog(Window dlg, string text, bool forward)
    {
        var edit = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        var vp = edit?.Patterns.Value.PatternOrDefault;
        if (vp is not null && !vp.IsReadOnly.ValueOrDefault) vp.SetValue(text);
        dlg.FindFirstDescendant(cf => cf.ByName(forward ? "Find Next" : "Find Previous"))?.AsButton().Invoke();
        Settle();
    }

    /// <summary>The concatenated text labels in a dialog (used to read the Find status message).</summary>
    public string DialogText(Window dlg)
        => string.Join(" | ", dlg.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name ?? "").Where(n => n.Length > 0));

    /// <summary>Gives a named element inside a dialog keyboard focus ("" = its text box), without taking the
    /// desktop's foreground.</summary>
    public void FocusInDialog(Window dlg, string name = "")
    {
        var element = name.Length == 0
            ? dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))
            : dlg.FindFirstDescendant(cf => cf.ByName(name));
        IntPtr hwnd = element?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero) GiveKeyboardFocus(hwnd);
        Settle();
    }

    /// <summary>Waits until a dialog's text labels contain <paramref name="substring"/> (find runs async).</summary>
    public bool WaitDialogText(Window dlg, string substring, int ms = 8000)
        => Retry.WhileFalse(() => DialogText(dlg).Contains(substring, StringComparison.OrdinalIgnoreCase),
               TimeSpan.FromMilliseconds(ms), Poll).Result;

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
               TimeSpan.FromMilliseconds(ms), Poll).Result;

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

    private AutomationElement? _menuBarCache;

    private AutomationElement? MenuBarOrNull()
        => _menuBarCache ??= Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar));

    private AutomationElement MenuBar()
        => Retry.WhileNull(MenuBarOrNull, TimeSpan.FromSeconds(5)).Result
           ?? throw new InvalidOperationException("Menu bar not found.");

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
        Settle();
        return true;
    }

    public void ClickMenuOrThrow(params string[] path)
    {
        if (!ClickMenu(path))
            throw new InvalidOperationException("MENU ACTION failed: " + string.Join(" > ", path) +
                                                ". The requested menu item was not found or could not be invoked.");
    }

    private bool? TryReadFilteredModeFromMenu()
    {
        var top = TopItems().FirstOrDefault(m => Norm(m.Name) == Norm("View"));
        if (top is null) return null;
        Expand(top);
        try
        {
            var item = FindOpenMenuItem("Show Only Filtered Lines");
            if (item is null) return null;

            var toggle = item.Patterns.Toggle.PatternOrDefault;
            if (toggle is not null)
                return toggle.ToggleState.ValueOrDefault == ToggleState.On;

            var legacy = item.Patterns.LegacyIAccessible.PatternOrDefault;
            if (legacy is not null)
                return legacy.State.ValueOrDefault.HasFlag(AccessibilityState.STATE_SYSTEM_CHECKED);

            return null;
        }
        finally { Collapse(top); Settle(1); }
    }

    private void WaitForFilteredMode(bool expected, int ms = 8000)
    {
        if (!Retry.WhileFalse(() => FilteredModeLooksLike(expected), TimeSpan.FromMilliseconds(ms), Poll).Result)
            throw new InvalidOperationException(
                $"Filtered-mode change did not reach expected state {expected}. " +
                $"Menu checked={TryReadFilteredModeFromMenu()?.ToString() ?? "unknown"}; " +
                $"visible rows: {VisibleRowSample()}");
    }

    private bool FilteredModeLooksLike(bool expected)
    {
        bool? menu = TryReadFilteredModeFromMenu();
        if (menu is not null) return menu.Value == expected;
        return VisibleRowsLookFiltered() == expected;
    }

    private bool RowsAreOnlyMatchLines()
    {
        var rows = Rows();
        if (rows.Length == 0) return false;
        foreach (var r in rows)
        {
            string name = r.Name ?? "";
            if (!name.Contains("MATCH", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private string VisibleRowSample()
        => string.Join(" | ", Rows().Take(8).Select(r =>
        {
            string line = r.Patterns.LegacyIAccessible.PatternOrDefault?.Value.ValueOrDefault ?? "?";
            string text = r.Name ?? "";
            return $"{line}:{(text.Length <= 40 ? text : text[..40])}";
        }));

    /// <summary>Selects the given 1-based file line via the grid's accessibility (no dialog/foreground).</summary>
    public void SelectLine(int oneBasedLine)
    {
        var row = Retry.WhileNull(() =>
            Rows().FirstOrDefault(r => r.Patterns.LegacyIAccessible.PatternOrDefault?.Value.ValueOrDefault == oneBasedLine.ToString()),
            TimeSpan.FromSeconds(3)).Result
            ?? throw new InvalidOperationException($"Row for line {oneBasedLine} not visible; on screen: {VisibleLineRange()}.");
        var la = row.Patterns.LegacyIAccessible.Pattern;
        la.Select(3); // SELFLAG_TAKEFOCUS | SELFLAG_TAKESELECTION
        // The grid reports selection through MSAA, not the SelectionItem pattern - see SelectedRowText.
        Retry.WhileFalse(() => la.State.ValueOrDefault.HasFlag(AccessibilityState.STATE_SYSTEM_SELECTED),
                         TimeSpan.FromSeconds(2), Poll);
        Settle(1);
    }

    /// <summary>Scrolls the log vertically by driving the grid's vertical scrollbar (UIA RangeValue),
    /// so an off-screen line can be brought into view. <paramref name="firstRow"/> is the display row
    /// to put at the top. Returns false if the view could not be moved there.</summary>
    public bool ScrollVerticalTo(int firstRow)
    {
        // Setting the value once and sleeping is not enough: on a small CI window the scrollbar element is
        // sometimes not exposed yet, and the grid repaints on its own 33ms timer. Silently returning false
        // then surfaced much later as "row not visible", which is a confusing way to learn the scroll never
        // happened - so confirm the view really moved, and retry if it did not.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var vbar = Grid().FindAllChildren(cf => cf.ByControlType(ControlType.ScrollBar))
                             .FirstOrDefault(s => s.BoundingRectangle.Height >= s.BoundingRectangle.Width);
            var rv = vbar?.Patterns.RangeValue.PatternOrDefault;
            if (rv is not null && !rv.IsReadOnly.ValueOrDefault)
            {
                rv.SetValue(firstRow);
                // Confirm through the scrollbar itself: FirstVisibleLine reports file line numbers, which
                // do not start at 1 in filtered mode, so it cannot answer "did we reach the top?".
                if (firstRow == 0)
                {
                    if (Retry.WhileFalse(() => rv.Value.ValueOrDefault <= 0.5,
                            TimeSpan.FromSeconds(2), Poll).Result)
                    {
                        Settle(1);
                        return true;
                    }
                }
                else if (Retry.WhileFalse(() => FirstVisibleLine() >= firstRow,
                             TimeSpan.FromSeconds(2), Poll).Result)
                    return true;
            }
            Settle(3);
        }
        return false;
    }

    /// <summary>How far the log view is scrolled sideways, read from the horizontal scrollbar.</summary>
    public double HorizontalScroll()
    {
        var hbar = Grid().FindAllChildren(cf => cf.ByControlType(ControlType.ScrollBar))
                         .FirstOrDefault(s => s.BoundingRectangle.Width > s.BoundingRectangle.Height)
                   ?? throw new InvalidOperationException("Horizontal scrollbar not found.");
        return hbar.Patterns.RangeValue.Pattern.Value.Value;
    }

    /// <summary>Waits for the horizontal scroll offset to satisfy <paramref name="predicate"/>.</summary>
    public bool WaitHorizontalScroll(Func<double, bool> predicate, int ms = 5000)
        => Retry.WhileFalse(() => predicate(HorizontalScroll()),
               TimeSpan.FromMilliseconds(ms), Poll).Result;

    /// <summary>Scrolls so <paramref name="row"/> sits in the middle of the viewport, whatever its height.    /// Tests must never assume a window size - CI screens are far smaller than a developer's monitor, so a
    /// hard-coded first row can leave the target off-screen. Throws if the view would not move, because
    /// every caller depends on it having worked.</summary>
    public void ScrollRowToMiddle(int row)
    {
        int visible = Math.Max(1, Rows().Length);
        int target = Math.Max(0, row - visible / 2);
        if (!ScrollVerticalTo(target))
            throw new InvalidOperationException(
                $"Could not scroll the log to row {target} (wanted line {row + 1} in view, " +
                $"{visible} rows visible, currently showing {VisibleLineRange()}).");
    }

    /// <summary>The lines currently on screen, as "first-last (count)" - used in failure messages.</summary>
    private string VisibleLineRange()
    {
        var lines = Rows()
            .Select(r => int.TryParse(r.Patterns.LegacyIAccessible.PatternOrDefault?.Value.ValueOrDefault, out int n) ? n : -1)
            .Where(n => n > 0).ToArray();
        return lines.Length == 0 ? "no rows" : $"{lines.Min()}-{lines.Max()} ({lines.Length} rows)";
    }

    private static void Expand(AutomationElement item)
    {
        var ec = item.Patterns.ExpandCollapse.PatternOrDefault;
        if (ec is not null) { try { ec.Expand(); } catch { /* fall through */ } }
        else item.Patterns.Invoke.PatternOrDefault?.Invoke();
        Settle(1);
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

    /// <summary>Menu items currently present: the top-level items plus any open dropdown's items, which
    /// WinForms surfaces underneath the menu bar. Searching the menu bar rather than the whole window
    /// matters enormously - a window-wide descendant search also visits every log row, which measured
    /// 365ms against 4ms here. The window is still searched if nothing matches, so an item hosted
    /// somewhere unexpected is found rather than silently missed.</summary>
    private AutomationElement[] OpenDropDownItems()
    {
        var items = MenuBar().FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem));
        return items.Length > 0 ? items : Window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem));
    }

    private AutomationElement? FindOpenMenuItem(string name)
        => Retry.WhileNull(() =>
           {
               var items = OpenDropDownItems();
               return items.FirstOrDefault(m => Norm(m.Name) == Norm(name))
                   ?? items.FirstOrDefault(m => Norm(m.Name).StartsWith(Norm(name), StringComparison.OrdinalIgnoreCase));
           },
           TimeSpan.FromSeconds(2), Poll).Result;

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

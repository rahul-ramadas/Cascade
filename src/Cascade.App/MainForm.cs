using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;
using Cascade.Core.Persistence;
using Cascade.Core.Updating;

namespace Cascade.App;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly MachineState _state;
    private readonly CascadeDocument _doc = new();
    private readonly LineGridControl _grid = new() { Dock = DockStyle.Fill };
    private readonly FilterTreeControl _filterTree = new() { Dock = DockStyle.Fill };
    private readonly FilterPresetsControl _presets = new() { Dock = DockStyle.Fill };
    // The filter list and its presets share one pane, split the short way round: the presets sit beside the
    // list when it is docked along the bottom or top, and beneath it when it is down one side.
    private readonly SplitContainer _filterPane = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel2 };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _srcLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _filterLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _busyLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _selLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _filLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _totalLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _lineLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _zoomLabel = new() { AutoSize = true };
    // Hidden until a downloaded update is waiting, which happens at most once in a session.
    // Lives at the right end of the menu bar, which is otherwise empty, so announcing an update does not
    // squeeze the status bar's paths. Not a menu item: it must never be focusable or open anything.
    // No AccessibleName - that would replace the text as the name UI Automation reports.
    private readonly ToolStripLabel _updateLabel = new()
    {
        Alignment = ToolStripItemAlignment.Right,
        Visible = false,
        Name = "stat.update",
        ForeColor = Color.SeaGreen,
        Overflow = ToolStripItemOverflow.Never
    };
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 30, AutoSize = false, Width = 120 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 33 };

    private ToolStripMenuItem _miFilteredMode = null!, _miLineNumbers = null!, _miMarkers = null!;
    private ToolStripMenuItem _miPresets = null!, _miMatchMap = null!;
    private ToolStripMenuItem _recentFilesMenu = null!, _recentFilterFilesMenu = null!;

    private FindDialog? _findDialog;
    private FindQuery? _lastQuery;
    private readonly FilterHistory _history = new();
    private ToolStripMenuItem _miUndo = null!, _miRedo = null!;
    private string? _filterFilePath;
    private bool _filtersDirty;
    private volatile bool _pendingRefresh;
    private long _lastRowCount = -1, _lastMatched = -1;
    private bool _lastBusy;
    private bool _anchorActive;
    private bool _findBusy;
    private Func<double>? _findProgress;
    private string _findWhat = "", _findWhatDetail = "", _findMsg = "", _findMsgDetail = "";
    private double _findFraction;
    private DateTime _findMsgUntil;
    // The tally walks every hit, so it is recomputed on a human timescale rather than on the 33ms tick.
    private string _tally = "", _tallyDetail = "";
    private DateTime _tallyAt;
    private long _tallyLine = -1;
    private int _tallyGeneration = -1;
    private int _activitySlot, _progressSlot;
    private bool _inStatusLayout;    private (string Path, int Width) _shownSrc, _shownFilter;
    private int _treePanel = 2; // which split panel holds the filter tree (for show/hide)

    /// <summary>Set by the headless screenshot harness: never prompt to save filters when closing. There is
    /// no user present to answer, so the modal prompt would block the render indefinitely.</summary>
    internal bool NoSavePrompt;
    private bool _offScreen;

    // Harness only: shows the update notice without an update actually being pending.
    internal string? UpdateNoticeOverride;

    /// <summary>Harness only: whether indexing or filtering is still running, so a render can wait for a
    /// settled window instead of a flat timeout.</summary>
    internal bool IsBusyForHarness => _doc.IsBusy;

    /// <summary>Null when updating is switched off. Only ever read here - the swap happens in Program after
    /// the message loop ends.</summary>
    private readonly UpdateService? _updater;

    private enum FilterDock { Bottom, Top, Left, Right }

    public MainForm(AppSettings settings, MachineState state, string[] args, UpdateService? updater = null)
    {
        _settings = settings;
        _state = state;
        _updater = updater;
        Text = "Cascade";
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(700, 400);
        Icon = LoadAppIcon();
        PlaceOffScreenIfAsked();

        BuildMenu();
        BuildStatusBar();

        _split.Panel1.Controls.Add(_grid);
        _filterPane.Panel1.Controls.Add(_filterTree);
        _filterPane.Panel2.Controls.Add(_presets);
        _split.Panel2.Controls.Add(_filterPane);
        Controls.Add(_split);
        Controls.Add(_status);
        _split.BringToFront();

        _grid.Attach(_doc, _settings);
        _filterTree.Attach(_doc);
        _presets.Attach(_doc);
        _filterTree.SetSettings(_settings);

        _doc.Updated += () => _pendingRefresh = true;
        _grid.SelectionChanged += UpdateStatus;
        _grid.ZoomChanged += () => { UpdateStatus(); SaveSettingsSoon(); };
        _filterTree.FiltersChanged += OnFiltersChanged;
        _filterTree.BeforeFiltersEdited += label => _history.Begin(label, _doc.Filters);
        _presets.PresetsApplied += () => { _filterTree.RefreshCheckStates(); OnFiltersChanged(); };
        _presets.PresetsEdited += () => { _filtersDirty = true; UpdateTitle(); };
        _filterTree.EditRequested += EditFilter;
        _filterTree.AddRequested += AddFilter;
        _filterTree.FindFilterRequested += FindFilterMatch;
        _filterTree.NoFilterMatch += q => NoMoreMatches("No more filters", $"No more filters matching {Quote(q)}");
        _grid.NoMoreMarkers += i => NoMoreMatches($"No more marker {i + 1}");

        _refreshTimer.Tick += (_, _) =>
        {
            if (_pendingRefresh) { _pendingRefresh = false; _grid.RefreshView(); _grid.InvalidateMatchMap(); _filterTree.RefreshCounts(); }
            else if (_doc.IsBusy) _filterTree.RefreshCounts();
            if (_anchorActive && !_doc.IsBusy) { _grid.RefreshView(); _grid.ClearViewAnchor(); _anchorActive = false; }
            UpdateStatusIfChanged();
            FlushConfig();
        };
        _refreshTimer.Start();

        Shown += (_, _) => ProcessArgs(args);
        FormClosing += OnClosing;
        SyncUndoMenu();
        UpdateStatus();
    }

    /// <summary>An automated UI run drives the app through UI Automation and needs no one to see it, but a
    /// maximised window landing on top of whatever the user is doing - and catching a stray click, which
    /// fails the run - is intolerable. CASCADE_TEST_OFFSCREEN parks it beyond the last monitor instead:
    /// invisible and unreachable by the mouse, while staying on the real desktop, where UI Automation is
    /// quick and dependable. (Running the whole suite on a Windows desktop of its own hides it just as well
    /// but was measured 5x slower and badly flaky - automation there waits out its transaction timeouts.)</summary>
    private void PlaceOffScreenIfAsked()
    {
        if (Environment.GetEnvironmentVariable("CASCADE_TEST_OFFSCREEN") != "1") return;
        _offScreen = true;
        var virtualScreen = SystemInformation.VirtualScreen;
        StartPosition = FormStartPosition.Manual;
        WindowState = FormWindowState.Normal;   // a maximised window is snapped back onto a monitor
        Location = new Point(virtualScreen.Right + 200, virtualScreen.Top);
    }

    /// <summary>Loads the embedded multi-resolution application icon; falls back to the system icon.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var asm = typeof(MainForm).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("cascade.ico", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s is not null) return new Icon(s); // keep all frames so each UI context picks its size
            }
        }
        catch { /* fall through to the default icon */ }
        return SystemIcons.Application;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Sized here, not in the constructor: asked any earlier the form has not scaled itself yet and
        // settles at MinimumSize, which squeezes the filter pane down to nothing.
        if (_offScreen) Size = new Size(1600, 1000);
        try { _split.SplitterDistance = (int)(ClientSize.Height * 0.7); } catch { /* size not ready */ }
        LayoutPresetPane();
        _grid.SetMatchMapVisible(_settings.ShowMatchMap);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Tab) { CycleFocus(forward: true); return true; }
        if (keyData == (Keys.Shift | Keys.Tab)) { CycleFocus(forward: false); return true; }
        if (keyData == Keys.Escape && _findBusy) { _doc.CancelFind(); return true; }
        // ...and once nothing is running, Esc puts the term away: highlights off and the counts with them.
        if (keyData == Keys.Escape && _lastQuery is not null && !IsTextInputFocused()) { ClearFind(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.L)) { ToggleFilterList(); return true; }
        if (!IsTextInputFocused())
        {
            switch (keyData)
            {
                // Undo/redo belong to the filter list wherever focus is, except inside a text box, where
                // Ctrl+Z has to keep meaning "undo my typing".
                case Keys.Control | Keys.Z: UndoFilterEdit(); return true;
                case Keys.Control | Keys.Y: RedoFilterEdit(); return true;
                case Keys.Control | Keys.Shift | Keys.Up: SetFilterDock(FilterDock.Top); return true;
                case Keys.Control | Keys.Shift | Keys.Down: SetFilterDock(FilterDock.Bottom); return true;
                case Keys.Control | Keys.Shift | Keys.Left: SetFilterDock(FilterDock.Left); return true;
                case Keys.Control | Keys.Shift | Keys.Right: SetFilterDock(FilterDock.Right); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- menu ----

    private static ToolStripMenuItem Mi(string text, EventHandler onClick, Keys keys = Keys.None, string? shortcutText = null)
    {
        var m = new ToolStripMenuItem(text, null, onClick);
        if (keys != Keys.None) m.ShortcutKeys = keys;
        // Override the displayed shortcut where the default enum name is ugly (e.g. "Oemplus").
        if (shortcutText is not null) m.ShortcutKeyDisplayString = shortcutText;
        return m;
    }

    /// <summary>A menu item that only advertises its shortcut; the key itself is handled by the control it
    /// applies to, so it is not registered as a form-wide ShortcutKeys.</summary>
    private static ToolStripMenuItem Hint(string text, string keys, Action onClick)
        => new(text, null, (_, _) => onClick()) { ShortcutKeyDisplayString = keys };

    private void BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Mi("&Open…", (_, _) => OpenFileDialogAndLoad(), Keys.Control | Keys.O));
        file.DropDownItems.Add(Mi("&Reload", (_, _) => Reload(), Keys.F5));
        file.DropDownItems.Add(Mi("Open from &Clipboard", (_, _) => OpenFromClipboard()));
        file.DropDownItems.Add(Mi("Save Current &Lines…", (_, _) => SaveCurrentLines()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Mi("Loa&d Filters…", (_, _) => LoadFilters()));
        file.DropDownItems.Add(Mi("&Save Filters", (_, _) => SaveFilters(false), Keys.Control | Keys.S));
        file.DropDownItems.Add(Mi("Save Filters &As…", (_, _) => SaveFilters(true)));
        file.DropDownItems.Add(Mi("A&ppend Filters…", (_, _) => AppendFilters()));
        file.DropDownItems.Add(Mi("&Import .tat filters…", (_, _) => ImportTat()));
        file.DropDownItems.Add(Mi("Clos&e Filters", (_, _) => CloseFilters()));
        file.DropDownItems.Add(new ToolStripSeparator());
        _recentFilesMenu = new ToolStripMenuItem("Recent &Files");
        _recentFilterFilesMenu = new ToolStripMenuItem("Recent Filter Files");
        file.DropDownItems.Add(_recentFilesMenu);
        file.DropDownItems.Add(_recentFilterFilesMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        var settingsMenu = new ToolStripMenuItem("Se&ttings");
        settingsMenu.DropDownItems.Add(Mi("&Export…", (_, _) => ExportSettings()));
        settingsMenu.DropDownItems.Add(Mi("&Import…", (_, _) => ImportSettings()));
        file.DropDownItems.Add(settingsMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Mi("E&xit", (_, _) => Close()));

        var edit = new ToolStripMenuItem("&Edit");
        _miUndo = Mi("Un&do", (_, _) => UndoFilterEdit(), Keys.Control | Keys.Z);
        _miRedo = Mi("R&edo", (_, _) => RedoFilterEdit(), Keys.Control | Keys.Y);
        edit.DropDownItems.Add(_miUndo);
        edit.DropDownItems.Add(_miRedo);
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Mi("&Copy", (_, _) => _grid.CopySelection(false), Keys.Control | Keys.C));
        edit.DropDownItems.Add(Mi("Copy with Line N&umbers", (_, _) => _grid.CopySelection(true)));
        edit.DropDownItems.Add(Mi("Select &All", (_, _) => _grid.SelectAll(), Keys.Control | Keys.A));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Mi("&Find…", (_, _) => ShowFind(), Keys.Control | Keys.F));
        edit.DropDownItems.Add(Mi("Find &Next", (_, _) => RepeatFind(true), Keys.F3));
        edit.DropDownItems.Add(Mi("Find &Previous", (_, _) => RepeatFind(false), Keys.Shift | Keys.F3));
        edit.DropDownItems.Add(Mi("&Go To Line…", (_, _) => GoTo(), Keys.Control | Keys.G));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Mi("P&references…", (_, _) => ShowPreferences()));

        var view = new ToolStripMenuItem("&View");
        _miFilteredMode = Mi("Show Only &Filtered Lines", (_, _) => ToggleFilteredMode(), Keys.Control | Keys.H);
        _miLineNumbers = new ToolStripMenuItem("Show &Line Numbers", null, (_, _) =>
        {
            _settings.ShowLineNumbers = !_settings.ShowLineNumbers;
            _miLineNumbers.Checked = _settings.ShowLineNumbers;
            _grid.RefreshView();
            SaveSettingsSoon();
        })
        { Checked = _settings.ShowLineNumbers };
        view.DropDownItems.Add(_miFilteredMode);
        view.DropDownItems.Add(_miLineNumbers);
        _miMatchMap = new ToolStripMenuItem("Show Matc&h Map", null, (_, _) =>
        {
            _settings.ShowMatchMap = !_settings.ShowMatchMap;
            _miMatchMap.Checked = _settings.ShowMatchMap;
            _grid.SetMatchMapVisible(_settings.ShowMatchMap);
            SaveSettingsSoon();
        })
        { Checked = _settings.ShowMatchMap, ShortcutKeys = Keys.Control | Keys.M };
        view.DropDownItems.Add(_miMatchMap);
        view.DropDownItems.Add(BuildMarkersMenu());
        view.DropDownItems.Add(Mi("&Columns…", (_, _) => ShowColumns()));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Mi("Zoom &In", (_, _) => _grid.Zoom(10), Keys.Control | Keys.Oemplus, "Ctrl++"));
        view.DropDownItems.Add(Mi("Zoom &Out", (_, _) => _grid.Zoom(-10), Keys.Control | Keys.OemMinus, "Ctrl+-"));
        view.DropDownItems.Add(Mi("&Reset Zoom", (_, _) => _grid.ResetZoom(), Keys.Control | Keys.D0));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Mi("Focus &Text Area", (_, _) => FocusTextArea(), Keys.Control | Keys.Shift | Keys.T));
        view.DropDownItems.Add(Mi("Foc&us Filter List", (_, _) => FocusFilterList(), Keys.Control | Keys.Shift | Keys.F));
        view.DropDownItems.Add(Mi("Focus Filter &Search", (_, _) => FocusFilterSearch(), Keys.Control | Keys.E));
        _miPresets = new ToolStripMenuItem("Show Filter &Presets", null, (_, _) =>
        {
            _settings.ShowFilterPresets = !_settings.ShowFilterPresets;
            _miPresets.Checked = _settings.ShowFilterPresets;
            LayoutPresetPane();
            SaveSettingsSoon();
        })
        { Checked = _settings.ShowFilterPresets, ShortcutKeys = Keys.Control | Keys.Shift | Keys.P };
        view.DropDownItems.Add(_miPresets);
        view.DropDownItems.Add(BuildFilterLocationMenu());
        view.DropDownItems.Add(BuildEncodingMenu());

        var filters = new ToolStripMenuItem("Fi&lters");
        filters.DropDownItems.Add(Mi("&Add Filter…", (_, _) => AddFilter(null)));
        filters.DropDownItems.Add(Mi("Add &Child Filter…", (_, _) => AddFilter(_filterTree.SelectedFilter)));
        filters.DropDownItems.Add(Mi("New Filter from Se&lection…", (_, _) => NewFilterFromSelection(), Keys.Control | Keys.N));
        filters.DropDownItems.Add(Mi("&Edit Filter…", (_, _) => { if (_filterTree.SelectedFilter is { } f) EditFilter(f); }));
        filters.DropDownItems.Add(Mi("Duplica&te Filter", (_, _) => _filterTree.DuplicateSelected(), Keys.Control | Keys.D));
        filters.DropDownItems.Add(Mi("&Remove Filter", (_, _) => _filterTree.RemoveSelected()));
        filters.DropDownItems.Add(new ToolStripSeparator());
        // The keys themselves are handled by the filter tree (they only apply while it has focus), so these
        // just advertise them.
        filters.DropDownItems.Add(Hint("Move &Up", "Ctrl+Up", () => _filterTree.MoveSelected(Keys.Up)));
        filters.DropDownItems.Add(Hint("Move &Down", "Ctrl+Down", () => _filterTree.MoveSelected(Keys.Down)));
        filters.DropDownItems.Add(Hint("&Indent (nest under filter above)", "Ctrl+Right", () => _filterTree.MoveSelected(Keys.Right)));
        filters.DropDownItems.Add(Hint("&Outdent", "Ctrl+Left", () => _filterTree.MoveSelected(Keys.Left)));
        filters.DropDownItems.Add(new ToolStripSeparator());
        filters.DropDownItems.Add(Mi("Find &Next Match", (_, _) => FindSelectedFilterMatch(true), Keys.F4));
        filters.DropDownItems.Add(Mi("Find Pre&vious Match", (_, _) => FindSelectedFilterMatch(false), Keys.Shift | Keys.F4));
        filters.DropDownItems.Add(new ToolStripSeparator());
        filters.DropDownItems.Add(Hint("Enable &Subtree", "Shift+Space", () => _filterTree.SetSelectedSubtreeEnabled(true)));
        filters.DropDownItems.Add(Hint("Disa&ble Subtree", "Shift+Space", () => _filterTree.SetSelectedSubtreeEnabled(false)));
        filters.DropDownItems.Add(new ToolStripSeparator());
        filters.DropDownItems.Add(Mi("Enable All", (_, _) => _filterTree.SetAllEnabled(true)));
        filters.DropDownItems.Add(Mi("Disable All", (_, _) => _filterTree.SetAllEnabled(false)));
        filters.DropDownItems.Add(Mi("Remove All", (_, _) => _filterTree.RemoveAll()));
        filters.DropDownItems.Add(new ToolStripSeparator());
        filters.DropDownItems.Add(BuildPresetsMenu());
        filters.DropDownItems.Add(new ToolStripSeparator());
        filters.DropDownItems.Add(Mi("&Find Filter", (_, _) => _filterTree.FocusSearch()));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Mi("&About Cascade", (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, filters, help, _updateLabel });
        MainMenuStrip = menu;
        Controls.Add(menu);
        RefreshRecentMenus();
    }

    /// <summary>The preset commands, so everything the pane's context menu offers is reachable from the
    /// keyboard too.</summary>
    private ToolStripMenuItem BuildPresetsMenu()
    {
        var m = new ToolStripMenuItem("&Presets");
        m.DropDownItems.Add(Mi("&Save Enabled Filters as Preset…", (_, _) => { EnsurePresetsVisible(); _presets.SaveCurrent(); }));
        m.DropDownItems.Add(Mi("&Update Preset from Enabled Filters", (_, _) => _presets.UpdateSelected()));
        m.DropDownItems.Add(Mi("&Rename Preset…", (_, _) => _presets.RenameSelected()));
        m.DropDownItems.Add(Mi("&Duplicate Preset", (_, _) => _presets.DuplicateSelected()));
        m.DropDownItems.Add(Mi("De&lete Preset", (_, _) => _presets.DeleteSelected()));
        return m;
    }

    private ToolStripMenuItem BuildMarkersMenu()
    {
        _miMarkers = new ToolStripMenuItem("Show &Markers");
        void Item(string text, MarkerVisibilityMode mode) =>
            _miMarkers.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) =>
            {
                _settings.MarkerVisibility = mode;
                SyncMarkersMenu();
                _grid.RefreshView();
                SaveSettingsSoon();
            })
            { Tag = mode });
        Item("Always", MarkerVisibilityMode.Always);
        Item("Never", MarkerVisibilityMode.Never);
        Item("When in use", MarkerVisibilityMode.WhenInUse);
        SyncMarkersMenu();
        return _miMarkers;
    }

    /// <summary>Ticks whichever marker mode is actually in effect. Preferences can change the same setting,
    /// so the menu has to re-read it rather than assume it still owns it.</summary>
    private void SyncMarkersMenu()
    {
        foreach (ToolStripMenuItem item in _miMarkers.DropDownItems)
            item.Checked = (MarkerVisibilityMode)item.Tag! == _settings.MarkerVisibility;
    }

    private ToolStripMenuItem BuildFilterLocationMenu()
    {
        var m = new ToolStripMenuItem("Filter List Loc&ation");
        // Show the key as a right-aligned hint (ShortcutKeyDisplayString); the keys themselves are
        // handled in ProcessCmdKey, so we don't register them via ShortcutKeys here.
        static ToolStripMenuItem Item(string text, string keys, EventHandler onClick)
            => new(text, null, onClick) { ShortcutKeyDisplayString = keys };
        m.DropDownItems.Add(Item("Dock &Bottom", "Ctrl+Shift+Down", (_, _) => SetFilterDock(FilterDock.Bottom)));
        m.DropDownItems.Add(Item("Dock &Top", "Ctrl+Shift+Up", (_, _) => SetFilterDock(FilterDock.Top)));
        m.DropDownItems.Add(Item("Dock &Left", "Ctrl+Shift+Left", (_, _) => SetFilterDock(FilterDock.Left)));
        m.DropDownItems.Add(Item("Dock &Right", "Ctrl+Shift+Right", (_, _) => SetFilterDock(FilterDock.Right)));
        m.DropDownItems.Add(new ToolStripSeparator());
        m.DropDownItems.Add(Item("Show/&Hide Filter List", "Ctrl+Shift+L", (_, _) => ToggleFilterList()));
        return m;
    }

    private ToolStripMenuItem BuildEncodingMenu()
    {
        var m = new ToolStripMenuItem("&Encoding");
        void Item(string text, Func<Encoding?> enc) =>
            m.DropDownItems.Add(text, null, (_, _) => ReopenWithEncoding(enc()));
        Item("Auto-detect", () => null);
        Item("UTF-8", () => new UTF8Encoding(false));
        Item("UTF-16 LE", () => new UnicodeEncoding(false, false));
        Item("UTF-16 BE", () => new UnicodeEncoding(true, false));
        Item("Windows-1252", () => { try { return Encoding.GetEncoding(1252); } catch { return null; } });
        Item("System default", () => Encoding.Default);
        return m;
    }

    private void BuildStatusBar()
    {
        // The sizing grip overlaps (and hides) the last status field; the window resizes via its border.
        _status.SizingGrip = false;
        // Stable names so UI Automation (the FlaUI tests) can locate each field.
        _srcLabel.Name = "stat.src";
        _filterLabel.Name = "stat.filter";
        _busyLabel.Name = "stat.busy";

        foreach (var l in new[] { _srcLabel, _filterLabel })
        {
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(Dpi(6), 0, Dpi(6), 0);
        }

        // One fixed-width slot for "what is happening": the bar appears inside it rather than next to it, so
        // nothing to its right ever moves when work starts or stops.
        _progress.Width = Dpi(70);
        _progress.Margin = new Padding(0, Dpi(4), Dpi(4), Dpi(4));
        _progress.Style = ProgressBarStyle.Continuous;
        _busyLabel.AutoSize = false;
        _busyLabel.TextAlign = ContentAlignment.MiddleLeft;
        _busyLabel.Margin = new Padding(Dpi(6), 0, Dpi(4), 0);
        _busyLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _progressSlot = _progress.Width + _progress.Margin.Horizontal;
        // Size the slot to the widest thing it will ever hold, so the wording is never clipped.
        _activitySlot = TextRenderer.MeasureText(WidestActivityText, _busyLabel.Font).Width + _progressSlot + Dpi(16);
        _busyLabel.Width = _activitySlot;

        // Each metric is a fixed box, so a value growing a digit never shifts its neighbours. The UI font is
        // kept: its digits are already all the same width, so a monospaced face would buy no extra stability
        // and would cost the paths ~140px of room.
        foreach (var l in new[] { _selLabel, _filLabel, _totalLabel, _lineLabel, _zoomLabel })
        {
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(Dpi(6), 0, Dpi(2), 0);
        }
        // Section dividers: counts, then position, then zoom.
        _selLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _lineLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _zoomLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;

        _selLabel.Name = "stat.sel";
        _filLabel.Name = "stat.fil";
        _totalLabel.Name = "stat.total";
        _lineLabel.Name = "stat.line";
        _zoomLabel.Name = "stat.zoom";
        _status.Items.AddRange(new ToolStripItem[]
        {
            // The label comes before the bar so the section's divider sits on an item whose left edge never
            // moves; with the bar first, hiding it dragged the divider left by the bar's width.
            _srcLabel, _filterLabel, _busyLabel, _progress,
            _selLabel, _filLabel, _totalLabel, _lineLabel, _zoomLabel
        });

        EnsureMetricWidths();
        // Spring widths only settle after a layout pass, so re-fit the paths whenever that changes.
        _status.SizeChanged += (_, _) => UpdateStatus();
    }

    private int Dpi(int v) => LogicalToDeviceUnits(v);

    /// <summary>Sizes each numeric field once and generously - a file with tens of millions of lines is
    /// assumed from the outset - so the boxes never grow while indexing counts up. Growing them would also
    /// shift the springing paths, which is what made the whole bar shuffle. Returns true if anything moved.
    /// </summary>
    private bool EnsureMetricWidths()
    {
        long actual = Math.Max(1_000, _doc.CompletedLineCount);
        long rounded = 9;
        while (rounded < actual) rounded = rounded * 10 + 9;

        long magnitude = Math.Max(99_999_999, rounded);
        // Only a window too narrow to afford that falls back to what this file actually needs.
        if (_status.Width > 0 && _status.Width - TotalMetricWidth(magnitude) - _activitySlot < Dpi(300))
            magnitude = rounded;

        var labels = MetricLabels;
        string[] samples = MetricSamples(magnitude);
        bool changed = false;
        for (int i = 0; i < labels.Length; i++)
        {
            int w = TextRenderer.MeasureText(samples[i], labels[i].Font).Width + Dpi(6);
            if (labels[i].Width != w) { labels[i].Width = w; changed = true; }
        }
        return changed;
    }

    private ToolStripStatusLabel[] MetricLabels => new[] { _selLabel, _filLabel, _totalLabel, _lineLabel, _zoomLabel };

    private static string[] MetricSamples(long magnitude)
    {
        string n = magnitude.ToString("N0");
        return new[] { $"Sel: {n}", $"Fil: {n}", $"Total: {n}", $"Ln: {n} / {n}", "Zoom: 400%" };
    }

    private int TotalMetricWidth(long magnitude)
    {
        var labels = MetricLabels;
        string[] samples = MetricSamples(magnitude);
        int total = 0;
        for (int i = 0; i < labels.Length; i++)
            total += TextRenderer.MeasureText(samples[i], labels[i].Font).Width + Dpi(6) + labels[i].Margin.Horizontal;
        return total;
    }

    /// <summary>Gives the filter slot its space only when there is a path to show. Returns true if it moved.</summary>
    private bool EnsureFilterSlot()
    {
        bool has = !string.IsNullOrEmpty(_filterFilePath);
        if (_filterLabel.Spring == has) return false;
        _filterLabel.Spring = has;
        if (!has)
            _filterLabel.Width = TextRenderer.MeasureText("(no filter file)", _filterLabel.Font).Width + Dpi(14);
        return true;
    }

    /// <summary>Shows as much of a path as fits, trimming the middle, with the whole thing on hover.</summary>
    private void SetPath(ToolStripStatusLabel label, string? path, string whenEmpty, ref (string Path, int Width) shown)
    {
        string full = path ?? "";
        int width = label.Width - label.Padding.Horizontal - Dpi(8);
        if (shown.Path == full && shown.Width == width) return;   // measuring is not free; only redo it when it matters
        shown = (full, width);

        if (full.Length == 0) { label.Text = whenEmpty; label.ToolTipText = ""; return; }
        label.ToolTipText = full;
        label.Text = Shorten(full, width, label.Font);
    }

    /// <summary>Trims a path to fit, keeping the file name and as much of the head as there is room for.</summary>
    private static string Shorten(string text, int maxWidth, Font font)
    {
        if (maxWidth <= 0) return "";
        if (TextRenderer.MeasureText(text, font).Width <= maxWidth) return text;

        int cut = text.LastIndexOfAny(new[] { '\\', '/' });
        string tail = cut > 0 ? text[cut..] : text;
        // Binary search the longest head that still leaves room for "head…tail".
        int lo = 0, hi = Math.Max(0, cut);
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (TextRenderer.MeasureText(text[..mid] + "\u2026" + tail, font).Width <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        string result = text[..lo] + "\u2026" + tail;
        if (TextRenderer.MeasureText(result, font).Width <= maxWidth) return result;

        // Even the file name alone does not fit: clip it from the left.
        for (int keep = tail.Length; keep > 0; keep--)
        {
            string candidate = "\u2026" + tail[^keep..];
            if (TextRenderer.MeasureText(candidate, font).Width <= maxWidth) return candidate;
        }
        return "\u2026";
    }

    // ---- file / open ----

    private void ProcessArgs(string[] args)
    {
        string? file = null, filterFile = null;
        bool demo = false;
        foreach (var a in args)
        {
            if (a.StartsWith("/Filters:", StringComparison.OrdinalIgnoreCase)) filterFile = a["/Filters:".Length..].Trim('"');
            else if (a.Equals("/demo", StringComparison.OrdinalIgnoreCase)) demo = true;
            else if (!a.StartsWith('/') && !a.StartsWith("--")) file = a;
        }
        if (file is not null && File.Exists(file)) OpenFile(file, null);

        // If no filter file was given on the command line, reload the one the user last had open.
        if (filterFile is null && _settings.AutoLoadLastFilterFile
            && !string.IsNullOrEmpty(_state.LastFilterFile) && File.Exists(_state.LastFilterFile))
            filterFile = _state.LastFilterFile;

        if (filterFile is not null && File.Exists(filterFile)) LoadFiltersFrom(filterFile);
        if (demo)
        {
            foreach (var f in _doc.Filters.Roots.Take(4)) f.Enabled = true;
            _filterTree.Rebuild();
            _filterTree.SelectFirst();
            OnFiltersChanged();
        }
    }

    private void OpenFileDialogAndLoad()
    {
        using var dlg = new OpenFileDialog { Title = "Open file", Filter = "All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenFile(dlg.FileName, null);
    }

    private void OpenFile(string path, Encoding? enc)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            _doc.Open(path, enc);
            _grid.Attach(_doc, _settings);
            _filterTree.Attach(_doc);
            _presets.Attach(_doc);
            _state.AddRecentFile(path);
            SaveStateSoon();
            RefreshRecentMenus();
            UpdateTitle();
            _lastRowCount = _lastMatched = -1;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open file:\n" + ex.Message, "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
    }

    private void Reload() { if (!string.IsNullOrEmpty(_doc.FilePath) && File.Exists(_doc.FilePath)) OpenFile(_doc.FilePath, null); }

    private void ReopenWithEncoding(Encoding? enc) { if (!string.IsNullOrEmpty(_doc.FilePath) && File.Exists(_doc.FilePath)) OpenFile(_doc.FilePath, enc); }

    private void OpenFromClipboard()
    {
        if (!Clipboard.ContainsText()) return;
        string text = Clipboard.GetText();
        string tmp = Path.Combine(Path.GetTempPath(), "cascade_clip_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        OpenFile(tmp, null);
    }

    private void SaveCurrentLines()
    {
        if (_doc.RowCount == 0) return;
        using var dlg = new SaveFileDialog { Filter = "Text (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*", FileName = "filtered.txt" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            using var writer = new StreamWriter(dlg.FileName, false, new UTF8Encoding(false));
            long rows = _doc.RowCount;
            for (long r = 0; r < rows; r++) writer.WriteLine(_doc.GetLineText(_doc.RowToLine(r)));
        }
        finally { Cursor = Cursors.Default; }
    }

    // ---- filters ----

    private void OnFiltersChanged()
    {
        // Every structural edit funnels through here, so this is where a snapshot taken before one becomes
        // an undo entry - or is dropped, when the tree turns out not to have changed.
        _history.Commit(_doc.Filters);
        SyncUndoMenu();
        // Which presets are in effect follows from which filters are enabled, so it is re-derived here
        // rather than tracked - ticking a filter by hand lights the matching preset up.
        _presets.RefreshActive();
        _filtersDirty = true;
        // Capture where the viewport is BEFORE the visible-line set changes, so the same line can be held at
        // the same place on screen while the new matches stream in.
        var anchor = _grid.CaptureViewAnchor();
        _doc.ApplyFilters();
        // In filtered mode the matched rows shift, so re-select the anchor line; in dim mode the row
        // set is unchanged, so leave any existing (possibly multi-row) selection intact.
        _grid.SetViewAnchor(anchor, select: _doc.FilteredMode);
        _grid.RefreshView();
        _anchorActive = anchor.IsValid;
        UpdateTitle();
        UpdateStatus();
    }

    private void UndoFilterEdit() => ApplyHistory(_history.Undo(_doc.Filters), "Nothing to undo");

    private void RedoFilterEdit() => ApplyHistory(_history.Redo(_doc.Filters), "Nothing to redo");

    /// <summary>Puts a restored tree on screen. Deliberately not routed through <see cref="OnFiltersChanged"/>
    /// for the history's sake - the snapshot has already been swapped onto the other stack, and committing
    /// again here would record undoing as an edit of its own.</summary>
    private void ApplyHistory(string? label, string emptyMessage)
    {
        if (label is null) { ShowFindMessage(emptyMessage); return; }
        _history.Abandon();
        _filterTree.Rebuild();
        _filtersDirty = true;
        var anchor = _grid.CaptureViewAnchor();
        _doc.ApplyFilters();
        _grid.SetViewAnchor(anchor, select: _doc.FilteredMode);
        _grid.RefreshView();
        _anchorActive = anchor.IsValid;
        SyncUndoMenu();
        UpdateTitle();
        UpdateStatus();
    }

    private void SyncUndoMenu()
    {
        _miUndo.Enabled = _history.CanUndo;
        _miRedo.Enabled = _history.CanRedo;
        _miUndo.Text = _history.UndoLabel is { } u ? $"Un&do {u}" : "Un&do";
        _miRedo.Text = _history.RedoLabel is { } r ? $"R&edo {r}" : "R&edo";
    }

    private void AddFilter(Filter? parent)
    {
        if (parent is not null && parent.Depth + 1 >= FilterCollection.MaxDepth)
        {
            MessageBox.Show(this, "Maximum nesting depth reached.", "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var filter = new Filter { Enabled = true };
        using var dlg = new FilterEditDialog(filter, isNew: true);
        _history.Begin("Add Filter", _doc.Filters);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _doc.Filters.Add(filter, parent);
            _filterTree.Rebuild();
            OnFiltersChanged();
        }
        else _history.Abandon();
    }

    private void EditFilter(Filter filter)
    {
        using var dlg = new FilterEditDialog(filter, isNew: false);
        _history.Begin("Edit Filter", _doc.Filters);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _filterTree.Rebuild();
            OnFiltersChanged();
        }
        else _history.Abandon();
    }

    /// <summary>What a filter made from a log line starts out matching: the line itself, less the space
    /// around it. Only the first 200 characters used to be kept, which threw away most of exactly the long
    /// lines that are worth building a filter from.</summary>
    internal static string SeedPatternFromLine(string line)
    {
        string text = line.Trim();
        return text.Length <= FilterEditDialog.MaxPatternLength ? text : text[..FilterEditDialog.MaxPatternLength];
    }

    private void CreateFilterFromLine(long line) => CreateFilterFrom(SeedPatternFromLine(_doc.GetLineText(line)));

    /// <summary>Ctrl+N: a filter from whatever is selected. Part of a line if part of one is selected -
    /// which is the point of being able to select part of one - and otherwise the whole caret line.</summary>
    private void NewFilterFromSelection()
    {
        if (_grid.SelectedText is { Length: > 0 } part)
        {
            CreateFilterFrom(SeedPatternFromLine(part));
            return;
        }
        long line = _grid.CaretLine;
        if (line < 0) { MessageBox.Show(this, "Select a line in the text view first.", "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        CreateFilterFromLine(line);
    }

    private void CreateFilterFrom(string pattern)
    {
        var filter = new Filter { Enabled = true, Match = { Text = pattern } };
        using var dlg = new FilterEditDialog(filter, isNew: true);
        _history.Begin("New Filter", _doc.Filters);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _doc.Filters.Add(filter);
            _filterTree.Rebuild();
            OnFiltersChanged();
        }
        else _history.Abandon();
    }

    private void FindSelectedFilterMatch(bool forward)
    {
        if (_filterTree.SelectedFilter is { } f) FindFilterMatch(f, forward);
    }

    private async void FindFilterMatch(Filter filter, bool forward)
    {
        if (string.IsNullOrEmpty(_doc.FilePath)) return;
        long caret = _grid.CaretLine;
        long start = caret < 0 ? (forward ? 0 : _doc.CompletedLineCount - 1) : caret + (forward ? 1 : -1);

        // A filter scan decodes and matches every line, which on a multi-gigabyte file takes long enough
        // that doing it inline would freeze the window with no sign of progress.
        SetFindBusy(true, "Searching", $"Searching for {Quote(filter.Match.Text)}");
        var progress = new Progress<double>(f => _findFraction = f);
        long found;
        try
        {
            found = await _doc.FindLineMatchingFilterAsync(filter, start, forward, progress);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer find, or cancelled with Esc. Only clear the bar for a genuine stop.
            if (!_doc.IsFindRunning) SetFindBusy(false);
            return;
        }
        SetFindBusy(false);

        if (found >= 0) GoToLine(found + 1);
        else NoMoreMatches("No more matches", $"No more matches for {Quote(filter.Match.Text)}");
    }

    /// <summary>Shared end-of-search feedback for every find command: a very short whole-window flash for
    /// something impossible to miss, a short reason in the status bar (the detail is on hover), and a beep.</summary>
    private void NoMoreMatches(string message, string? detail = null)
    {
        AppFlash.Flash(this);
        ShowFindMessage(message, detail);
        System.Media.SystemSounds.Beep.Play();
    }

    /// <summary>Puts a short-lived message in the status bar (it expires in <see cref="FindMessageSeconds"/>
    /// seconds, cleared by the regular status refresh).</summary>
    private void ShowFindMessage(string message, string? detail = null)
    {
        _findMsg = message;
        _findMsgDetail = detail ?? message;
        _findMsgUntil = DateTime.UtcNow.AddSeconds(FindMessageSeconds);
        UpdateStatus();
    }

    private void SetFindBusy(bool busy, string? what = null, string? detail = null, Func<double>? progress = null)
    {
        _findBusy = busy;
        _findProgress = busy ? progress : null;
        _findWhat = busy ? what ?? "Searching" : "";
        _findWhatDetail = busy ? detail ?? _findWhat : "";
        _findFraction = 0;
        if (busy) _findMsg = "";   // a new search supersedes the last "no more matches"
        UpdateStatus();
    }

    /// <summary>Quotes a search term for a message, shortened so the status bar stays readable.</summary>
    internal static string Quote(string term)
        => term.Length <= 40 ? $"\u201c{term}\u201d" : $"\u201c{term[..40]}\u2026\u201d";

    private const int FindMessageSeconds = 5;

    /// <summary>The longest wording the activity slot has to fit; it is sized from this. A long tally is
    /// ellipsised instead, with the whole of it on hover - the position and the total come first, so what is
    /// trimmed is the least of it.</summary>
    private const string WidestActivityText = "Match 9,999 of 99,999 lines";

    private void LoadFilters()
    {
        using var dlg = new OpenFileDialog { Filter = "Cascade/TAT filters (*.cascade;*.tat)|*.cascade;*.tat|Cascade (*.cascade)|*.cascade|TAT (*.tat)|*.tat|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadFiltersFrom(dlg.FileName);
    }

    private void LoadFiltersFrom(string path)
    {
        try
        {
            if (path.EndsWith(".tat", StringComparison.OrdinalIgnoreCase))
            {
                _doc.SetFilters(TatImporter.Import(path));
                _filterFilePath = null; // .tat is import-only
            }
            else
            {
                var (filters, cols) = CascadeFile.Load(path);
                _doc.SetFilters(filters);
                if (cols is not null) { _doc.Columns.Columns.Clear(); foreach (var c in cols.Columns) _doc.Columns.Columns.Add(c); CopyColumnSpec(cols, _doc.Columns); }
                _filterFilePath = path;
                _state.AddRecentFilterFile(path);
            }
            _filtersDirty = false;
            _history.Clear();
            SyncUndoMenu();
            _state.LastFilterFile = path; // remember for auto-load next launch
            _state.Save();
            _filterTree.Attach(_doc);
            _presets.Attach(_doc);
            SyncFilteredModeMenu();
            _grid.RefreshView();
            RefreshRecentMenus();
            UpdateTitle();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not load filters:\n" + ex.Message, "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void CopyColumnSpec(ColumnSpec from, ColumnSpec to)
    {
        to.Enabled = from.Enabled; to.Mode = from.Mode; to.Delimiter = from.Delimiter;
        to.CollapseConsecutive = from.CollapseConsecutive; to.MaxSplits = from.MaxSplits; to.Template = from.Template;
    }

    private void AppendFilters()
    {
        using var dlg = new OpenFileDialog { Filter = "Cascade/TAT filters (*.cascade;*.tat)|*.cascade;*.tat|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var loaded = dlg.FileName.EndsWith(".tat", StringComparison.OrdinalIgnoreCase)
                ? TatImporter.Import(dlg.FileName)
                : CascadeFile.Load(dlg.FileName).Filters;
            _history.Begin("Append Filters", _doc.Filters);
            foreach (var root in loaded.Roots.ToList()) _doc.Filters.Add(root.Clone());
            _filtersDirty = true;
            _filterTree.Attach(_doc);
            _presets.Attach(_doc);
            OnFiltersChanged();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ImportTat()
    {
        using var dlg = new OpenFileDialog { Filter = "TAT filters (*.tat)|*.tat|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadFiltersFrom(dlg.FileName);
    }

    /// <summary>Detaches the current filter file: clears all filters, forgets the file path, and stops it
    /// from being auto-loaded next launch. (Save first if you want to keep the current set.)</summary>
    private void CloseFilters()
    {
        _doc.SetFilters(new FilterCollection());
        _history.Clear();
        SyncUndoMenu();
        _filterFilePath = null;
        _filtersDirty = false;
        _state.LastFilterFile = null;
        _state.Save();
        _filterTree.Attach(_doc);
        _presets.Attach(_doc);
        SyncFilteredModeMenu();
        _grid.RefreshView();
        RefreshRecentMenus();
        UpdateTitle();
        UpdateStatus();
    }

    private void SaveFilters(bool saveAs)
    {
        string? path = _filterFilePath;
        if (saveAs || path is null || !path.EndsWith(".cascade", StringComparison.OrdinalIgnoreCase))
        {
            using var dlg = new SaveFileDialog { Filter = "Cascade filters (*.cascade)|*.cascade", FileName = Path.GetFileNameWithoutExtension(path) ?? "filters" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        try
        {
            CascadeFile.Save(path, _doc.Filters, _doc.Columns);
            _filterFilePath = path;
            _filtersDirty = false;
            _state.AddRecentFilterFile(path);
            _state.LastFilterFile = path; // remember for auto-load next launch
            _state.Save();
            RefreshRecentMenus();
            UpdateTitle();
            UpdateStatus();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ---- view ----

    private void ToggleFilteredMode()
    {
        // Capture the anchor in the CURRENT mode before flipping, so it maps the current row correctly.
        var anchor = _grid.CaptureViewAnchor();
        _doc.Filters.ShowOnlyFilteredLines = !_doc.Filters.ShowOnlyFilteredLines;
        _filtersDirty = true;
        SyncFilteredModeMenu();
        // Filtered vs. dim is a display-only mode: the matched set is unchanged, so there is no need to
        // re-run filtering (which would blank the view). Just re-map the view, holding the line where it
        // already is - the same as every filter change does.
        _grid.SetViewAnchor(anchor, select: true);
        _grid.RefreshView();
        _anchorActive = anchor.IsValid;
        UpdateStatus();
    }

    private void SyncFilteredModeMenu() => _miFilteredMode.Checked = _doc.Filters.ShowOnlyFilteredLines;

    /// <summary>Shows or hides the presets pane and gives it a sensible share of the filter pane. It takes
    /// the short side, so the split is measured across whichever way the pane is currently turned.</summary>
    private void LayoutPresetPane()
    {
        _filterPane.Panel2Collapsed = !_settings.ShowFilterPresets;
        if (!_settings.ShowFilterPresets) return;
        int total = _filterPane.Orientation == Orientation.Vertical ? _filterPane.Width : _filterPane.Height;
        int wanted = _filterPane.Orientation == Orientation.Vertical ? LogicalToDeviceUnits(200) : LogicalToDeviceUnits(120);
        int distance = Math.Max(1, total - wanted - _filterPane.SplitterWidth);
        try { if (distance > 0) _filterPane.SplitterDistance = distance; }
        catch { /* sizes not ready */ }
    }

    private void EnsurePresetsVisible()
    {
        if (_settings.ShowFilterPresets) return;
        _settings.ShowFilterPresets = true;
        _miPresets.Checked = true;
        LayoutPresetPane();
    }

    private void SetFilterDock(FilterDock dock)
    {
        bool treeFirst = dock is FilterDock.Top or FilterDock.Left;
        var orientation = dock is FilterDock.Left or FilterDock.Right ? Orientation.Vertical : Orientation.Horizontal;
        int wantedPanel = treeFirst ? 1 : 2;

        WithoutRedraw(() =>
        {
            _split.SuspendLayout();
            _split.Panel1Collapsed = false;
            _split.Panel2Collapsed = false;

            // Re-parenting a control destroys and recreates its window handle, which is what makes the whole
            // app flash. Top<->Left and Bottom<->Right keep the tree on the same side, so only swap the
            // panels when the sides genuinely change; otherwise just turn the splitter.
            if (_treePanel != wantedPanel)
            {
                _split.Panel1.Controls.Clear();
                _split.Panel2.Controls.Clear();
                if (treeFirst) { _split.Panel1.Controls.Add(_filterPane); _split.Panel2.Controls.Add(_grid); }
                else { _split.Panel1.Controls.Add(_grid); _split.Panel2.Controls.Add(_filterPane); }
                _treePanel = wantedPanel;
            }

            _split.Orientation = orientation;
            // Keep the presets on the pane's short side: beside the list when it spans the window, below it
            // when it is down one edge.
            _filterPane.Orientation = orientation == Orientation.Vertical ? Orientation.Horizontal : Orientation.Vertical;
            LayoutPresetPane();

            int total = orientation == Orientation.Vertical ? _split.Width : _split.Height;
            int treeSize = Math.Max(60, (int)(total * 0.3));
            try { _split.SplitterDistance = treeFirst ? treeSize : Math.Max(1, total - treeSize - _split.SplitterWidth); }
            catch { /* sizes not ready */ }
            _split.ResumeLayout();
        });
    }

    /// <summary>Applies a layout change with painting switched off for the whole window, so it shows the
    /// finished result in one go instead of every intermediate state. SuspendLayout alone is not enough:
    /// it batches layout, not painting.</summary>
    private void WithoutRedraw(Action change)
    {
        if (!IsHandleCreated) { change(); return; }
        SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try { change(); }
        finally
        {
            SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero,
                         RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);
        }
    }

    private const int WM_SETREDRAW = 0x000B;
    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_FRAME = 0x0400;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private void ToggleFilterList()
    {
        if (_treePanel == 1) _split.Panel1Collapsed = !_split.Panel1Collapsed;
        else _split.Panel2Collapsed = !_split.Panel2Collapsed;
    }

    private void FocusTextArea() => _grid.Focus();

    private void FocusFilterList() { EnsureFilterListVisible(); _filterTree.FocusList(); }

    private void FocusFilterSearch() { EnsureFilterListVisible(); _filterTree.FocusSearch(); }

    /// <summary>Tab / Shift+Tab cycles focus between the three main areas: log view → filter search → filter list.</summary>
    private void CycleFocus(bool forward)
    {
        int current = _grid.Focused ? 0 : _filterTree.SearchHasFocus ? 1 : _filterTree.ListHasFocus ? 2 : 0;
        int next = ((forward ? current + 1 : current - 1) % 3 + 3) % 3;
        switch (next)
        {
            case 1: FocusFilterSearch(); break;
            case 2: FocusFilterList(); break;
            default: FocusTextArea(); break;
        }
    }

    private void EnsureFilterListVisible()
    {
        if (_treePanel == 1) _split.Panel1Collapsed = false;
        else _split.Panel2Collapsed = false;
    }

    private bool IsTextInputFocused()
    {
        Control? c = ActiveControl;
        while (c is ContainerControl cont && cont.ActiveControl is not null) c = cont.ActiveControl;
        return c is TextBoxBase or ComboBox or NumericUpDown;
    }

    private void ShowColumns()
    {
        string sample = _doc.CompletedLineCount > 0 ? _doc.GetLineText(Math.Max(0, _grid.CaretLine < 0 ? 0 : _grid.CaretLine)) : "";
        using var dlg = new ColumnsDialog(_doc.Columns, sample);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            CopyColumnSpec(dlg.Result, _doc.Columns);
            _doc.Columns.Columns.Clear();
            foreach (var c in dlg.Result.Columns) _doc.Columns.Columns.Add(c);
            _grid.RefreshView();
        }
    }

    private void ShowPreferences()
    {
        using var dlg = new PreferencesDialog(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK) { ApplySettingsEverywhere(); SaveSettingsSoon(); }
    }

    /// <summary>Pushes the current settings into every part of the window that reads them, and re-ticks the
    /// menus that mirror one. Used after Preferences and after importing a settings file.</summary>
    private void ApplySettingsEverywhere()
    {
        _miLineNumbers.Checked = _settings.ShowLineNumbers;
        _miPresets.Checked = _settings.ShowFilterPresets;
        _miMatchMap.Checked = _settings.ShowMatchMap;
        _grid.SetMatchMapVisible(_settings.ShowMatchMap);
        LayoutPresetPane();
        SyncMarkersMenu();
        _grid.ApplySettings(_settings);
        _filterTree.SetSettings(_settings);
        RefreshRecentMenus();
        UpdateStatus();
    }

    private void ExportSettings()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export settings",
            Filter = "Cascade settings (*.json)|*.json|All files (*.*)|*.*",
            FileName = "cascade-settings.json"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { _settings.ExportTo(dlg.FileName); }
        catch (Exception ex) { Warn("Could not export settings", ex); }
    }

    private void ImportSettings()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import settings",
            Filter = "Cascade settings (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _settings.ImportFrom(dlg.FileName);
            _settings.Save();
            ApplySettingsEverywhere();
        }
        catch (Exception ex) { Warn("Could not import settings", ex); }
    }

    private void Warn(string what, Exception ex) =>
        MessageBox.Show(this, $"{what}:\n\n{ex.Message}", "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // ---- find / goto ----

    private void ShowFind()
    {
        if (_findDialog is null)
        {
            _findDialog = new FindDialog(DoFind);
            _findDialog.CancelRequested += () => _doc.CancelFind();
            // Typing marks the hits already on screen and nothing else: no sweep, and the view does not
            // move until a search is actually asked for.
            _findDialog.PreviewChanged += q =>
            {
                _grid.SetFindHighlight(q is null ? null : FindEngine.CompileQuery(q));
                if (q is null && _lastQuery is not null) ClearFind();
            };
        }
        _findDialog.SetHistory(_state.RecentFindTerms);
        if (!_findDialog.Visible) _findDialog.Show(this);
        _findDialog.FocusInput();
    }

    private async void DoFind(FindQuery query, bool forward)
    {
        _lastQuery = query;
        _state.AddRecentFindTerm(query.Text);
        _stateDirty = true;
        _findDialog?.SetHistory(_state.RecentFindTerms);
        // The highlight outlives the dialog: F3 keeps working with it closed, so the hits have to stay
        // marked until the term is deliberately put away.
        _grid.SetFindHighlight(FindEngine.CompileQuery(query));
        long start = _grid.CaretLine;
        start = start < 0 ? 0 : start + (forward ? 1 : -1);

        // F3 usually repeats the search with the dialog closed, so the progress has to reach the status bar
        // too - a search that is waiting on the sweep would otherwise look like the window had locked up.
        SetFindBusy(true, "Searching", $"Searching for {Quote(query.Text)}", () => _doc.FindProgressFor(forward));
        _findDialog?.SetSearching(true);
        long found;
        try
        {
            found = await _doc.FindNextAsync(query, start, forward);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled, or a newer search superseded this one. Only reset when nothing is still
            // running (i.e. a genuine cancel, not a supersede that already re-armed it).
            if (!_doc.IsFindRunning)
            {
                SetFindBusy(false);
                _findDialog?.SetSearching(false);
                _findDialog?.SetStatus("Canceled.");
            }
            return;
        }
        SetFindBusy(false);
        _findDialog?.SetSearching(false);
        if (found >= 0) { GoToLine(found + 1); _findDialog?.SetStatus(""); }
        else
        {
            _findDialog?.SetStatus(_doc.IsIndexComplete ? "Not found." : "Not found yet \u2014 file still loading\u2026");
            NoMoreMatches(_doc.IsIndexComplete ? "No more matches" : "No more matches yet",
                _doc.IsIndexComplete
                    ? $"No more matches for {Quote(query.Text)}"
                    : $"No more matches yet for {Quote(query.Text)} \u2014 still loading");
        }
    }

    private void RepeatFind(bool forward) { if (_lastQuery is { } q) DoFind(q, forward); else ShowFind(); }

    /// <summary>Drops the find term: highlights off, counts gone, and the sweep behind it released.</summary>
    private void ClearFind()
    {
        _lastQuery = null;
        _grid.SetFindHighlight(null);
        _doc.DropSearch();
        _findMsg = "";
        _tally = _tallyDetail = "";
        _tallyLine = -1;
        _findDialog?.SetStatus("");
        UpdateStatus();
    }

    /// <summary>The "Match 12 of 348" text, recomputed when there is reason to: counting walks every hit, so
    /// doing it on the 33ms tick would cost more than the answer is worth.</summary>
    private string RefreshTally()
    {
        if (_lastQuery is not { } query) return "";
        long caret = _grid.CaretLine;
        bool stale = caret != _tallyLine
                     || _tallyGeneration != _doc.FilterGeneration
                     || (!_doc.FindComplete && DateTime.UtcNow - _tallyAt > TimeSpan.FromMilliseconds(250))
                     || _tally.Length == 0;
        if (!stale) return _tally;

        _tallyLine = caret;
        _tallyGeneration = _doc.FilterGeneration;
        _tallyAt = DateTime.UtcNow;
        if (_doc.FindTally(caret) is not { } t) { _tally = _tallyDetail = ""; return ""; }
        _tally = FindStatusText.Short(t);
        _tallyDetail = FindStatusText.Long(t, query.Text);
        return _tally;
    }

    /// <summary>Writes the activity slot's text, trimming it to the space reserved for it. The untrimmed
    /// wording stays available on hover.</summary>
    private void SetActivity(string text, Color color, string? detail = null)
    {
        _busyLabel.ForeColor = color;
        _busyLabel.ToolTipText = detail ?? text;
        string fitted = text.Length == 0
            ? ""
            : ShortenEnd(text, _busyLabel.Width - _busyLabel.Padding.Horizontal - Dpi(8), _busyLabel.Font);
        if (_busyLabel.Text != fitted) _busyLabel.Text = fitted;
    }

    /// <summary>Trims plain text from the end. (Paths are trimmed in the middle instead, because their last
    /// segment is the part worth keeping - doing that to a status message eats the word and keeps the
    /// number.)</summary>
    private static string ShortenEnd(string text, int maxWidth, Font font)
    {
        if (maxWidth <= 0) return "";
        if (TextRenderer.MeasureText(text, font).Width <= maxWidth) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (TextRenderer.MeasureText(text[..mid] + "\u2026", font).Width <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return lo == 0 ? "\u2026" : text[..lo] + "\u2026";
    }

    private void SetProgress(ProgressBarStyle style, double fraction)
    {
        if (_progress.Style != style) _progress.Style = style;
        if (style != ProgressBarStyle.Continuous) return;
        _progress.Maximum = 1000;
        int v = (int)Math.Clamp(fraction * 1000, 0, 1000);
        // Set from just above: Windows slides the fill towards a rising value and lags badly behind a fast
        // job, but a step DOWN lands at once. Same reason as the find dialog's bar.
        if (v < _progress.Maximum) { _progress.Value = v + 1; _progress.Value = v; }
        else _progress.Value = v;
    }

    private void GoTo()
    {
        using var dlg = new GoToDialog(Math.Max(1, _doc.CompletedLineCount), Math.Max(1, _grid.CaretLine + 1));
        if (dlg.ShowDialog(this) == DialogResult.OK) GoToLine(dlg.LineNumber);
    }

    private void GoToLine(long oneBased)
    {
        long line = Math.Clamp(oneBased - 1, 0, Math.Max(0, _doc.CompletedLineCount - 1));
        _grid.GoToLine(line);
    }

    // ---- status ----

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!NoSavePrompt && _filtersDirty && _filterFilePath is not null)
        {
            var r = MessageBox.Show(this, "Save changes to filters?", "Cascade", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes) SaveFilters(false);
        }
        _refreshTimer.Stop();
        _settingsDirty = _stateDirty = true;   // a clean exit rewrites both, as it always has
        FlushConfig(force: true);
        _doc.Dispose();
    }

    // Preferences and recent files are written as they change, coalesced onto the refresh timer, because
    // waiting for FormClosing loses the lot when the app is ended from Task Manager - which is a
    // reasonable way to skip the save-filters prompt.
    private bool _settingsDirty, _stateDirty;
    private long _lastConfigSave;

    private void SaveSettingsSoon() => _settingsDirty = true;
    private void SaveStateSoon() => _stateDirty = true;

    private void FlushConfig(bool force = false)
    {
        if (!_settingsDirty && !_stateDirty) return;
        if (!force && Environment.TickCount64 - _lastConfigSave < 1000) return;   // Ctrl+Wheel zooms in bursts
        _lastConfigSave = Environment.TickCount64;
        if (_settingsDirty) { _settingsDirty = false; _settings.Save(); }
        if (_stateDirty) { _stateDirty = false; _state.Save(); }
    }

    private void UpdateStatusIfChanged()
    {
        if (_doc.RowCount != _lastRowCount || _doc.MatchedLineCount != _lastMatched
            || _doc.IsBusy || _doc.IsBusy != _lastBusy
            || _findBusy || _findMsg.Length > 0 || _lastQuery is not null
            || (_updater?.PendingVersion is not null || UpdateNoticeOverride is not null) != _updateLabel.Visible)
            UpdateStatus();
    }

    private void UpdateStatus()
    {
        _lastRowCount = _doc.RowCount;
        _lastMatched = _doc.MatchedLineCount;
        _lastBusy = _doc.IsBusy;

        // Settle every fixed width BEFORE measuring the paths: the springs only get their real size after a
        // layout pass, and measuring against a stale width leaves a path needlessly truncated.
        bool structural = EnsureMetricWidths();
        structural |= EnsureFilterSlot();
        if (structural && !_inStatusLayout)
        {
            _inStatusLayout = true;
            try { _status.PerformLayout(); } finally { _inStatusLayout = false; }
        }
        SetPath(_srcLabel, _doc.FilePath, "(no file)", ref _shownSrc);
        SetPath(_filterLabel, _filterFilePath, "(no filter file)", ref _shownFilter);

        bool hasFile = !string.IsNullOrEmpty(_doc.FilePath);
        bool indexing = hasFile && !_doc.IsIndexComplete;
        bool filtering = hasFile && _doc.IsIndexComplete && !_doc.IsFilterIdle;

        // ---- the activity slot: one fixed-width region, so nothing to its right ever moves ----
        if (_findMsg.Length > 0 && DateTime.UtcNow > _findMsgUntil) _findMsg = "";

        bool showBar = _findBusy || indexing || filtering;
        if (showBar != _progress.Visible)
        {
            _progress.Visible = showBar;
            _busyLabel.Width = _activitySlot - (showBar ? _progressSlot : 0);
        }

        if (_findBusy)
        {
            // A find is what the user just asked for, so it wins the slot.
            double fraction = _findProgress?.Invoke() ?? _findFraction;
            SetActivity($"{_findWhat}\u2026 {fraction * 100:F0}%  (Esc)", SystemColors.ControlText, _findWhatDetail);
            SetProgress(ProgressBarStyle.Continuous, fraction);
            // Fed from the same tick as the status bar so the two can never disagree. The dialog ignores
            // this unless its own bar is showing, so a per-filter find does not drive it.
            _findDialog?.SetProgress(fraction);
        }
        else if (_findMsg.Length > 0)
        {
            SetActivity(_findMsg, Color.Firebrick, _findMsgDetail);
            if (showBar) SetProgress(ProgressBarStyle.Continuous, Fraction(indexing, filtering));
        }
        else if (RefreshTally() is { Length: > 0 } tally)
        {
            SetActivity(tally, SystemColors.ControlText, _tallyDetail);
            if (showBar) SetProgress(ProgressBarStyle.Continuous, Fraction(indexing, filtering));
        }
        else if (indexing)
        {
            // The line count is already in the Total field, and the total is unknown until indexing ends,
            // so there is nothing useful to add here beyond the fact that it is running.
            SetActivity("Indexing\u2026", SystemColors.ControlText, $"Indexing\u2026 {_doc.CompletedLineCount:N0} lines so far");
            SetProgress(ProgressBarStyle.Marquee, 0);
        }
        else if (filtering)
        {
            double done = Fraction(indexing, filtering);
            SetActivity($"Filtering\u2026 {done * 100:F0}%", SystemColors.ControlText,
                $"Filtering\u2026 {_doc.FilterProcessedLineCount:N0} of {_doc.CompletedLineCount:N0} lines");
            SetProgress(ProgressBarStyle.Continuous, done);
        }
        else
        {
            SetActivity("", SystemColors.ControlText);
        }

        double Fraction(bool ix, bool ft)
        {
            if (!ft) return 0;
            long total = Math.Max(1, _doc.CompletedLineCount);
            return Math.Clamp(_doc.FilterProcessedLineCount / (double)total, 0, 1);
        }

        _selLabel.Text = $"Sel: {_grid.SelectedCount:N0}";
        _filLabel.Text = $"Fil: {_doc.MatchedLineCount:N0}";
        _totalLabel.Text = $"Total: {_doc.CompletedLineCount:N0}";
        long caretLine = _grid.CaretLine;
        _lineLabel.Text = caretLine >= 0 ? $"Ln: {caretLine + 1:N0} / {_doc.CompletedLineCount:N0}" : "Ln: \u2014";
        _zoomLabel.Text = $"Zoom: {_settings.ZoomPercent}%";

        // A downloaded update is worth saying once, quietly, and leaving on show.
        string? notice = UpdateNoticeOverride
                         ?? (_updater?.PendingVersion is { } pending ? $"Will update to v{pending} on restart" : null);
        if (notice is not null)
        {
            _updateLabel.Text = notice;
            _updateLabel.ToolTipText = "This version has been downloaded and will be installed when this window closes.";
            _updateLabel.Visible = true;
        }
        else if (_updateLabel.Visible)
        {
            _updateLabel.Visible = false;
        }
    }
    private void UpdateTitle()
    {
        string file = string.IsNullOrEmpty(_doc.FilePath) ? "" : " — " + Path.GetFileName(_doc.FilePath);
        string filt = _filterFilePath is not null ? $" [{Path.GetFileName(_filterFilePath)}{(_filtersDirty ? " *" : "")}]" : (_filtersDirty ? " [filters *]" : "");
        Text = "Cascade" + file + filt;
    }

    private void RefreshRecentMenus()
    {
        void Fill(ToolStripMenuItem menu, List<string> items, Action<string> open, string clearText, Action clear)
        {
            menu.DropDownItems.Clear();
            foreach (var p in items) menu.DropDownItems.Add(p, null, (_, _) => { if (File.Exists(p)) open(p); });
            if (items.Count > 0)
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
                menu.DropDownItems.Add(clearText, null, (_, _) => clear());
            }
            menu.Enabled = items.Count > 0;
        }
        Fill(_recentFilesMenu, _state.RecentFiles, p => OpenFile(p, null), "Clear Recent Files",
            () => { _state.RecentFiles.Clear(); _state.Save(); RefreshRecentMenus(); });
        Fill(_recentFilterFilesMenu, _state.RecentFilterFiles, LoadFiltersFrom, "Clear Recent Filter Files",
            () => { _state.RecentFilterFiles.Clear(); _state.Save(); RefreshRecentMenus(); });
    }

    private void ShowAbout()
    {
        using var d = new AboutDialog(_updater);
        d.ShowDialog(this);
    }
}

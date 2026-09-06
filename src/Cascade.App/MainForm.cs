using System.Buffers;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;
using Cascade.Core.Persistence;
using Cascade.Core.Text;
using Cascade.Core.Timing;
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
    private readonly StatusStrip _status = new() { ShowItemToolTips = true };
    private readonly ToolStripStatusLabel _srcLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _filterLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _busyLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _selLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _elapsedLabel = new() { AutoSize = true, Visible = false };
    private readonly ToolStripStatusLabel _filLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _totalLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _showLabel = new() { AutoSize = true };
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
    // Hidden unless a crop is in force. Centred in the menu bar, which is otherwise empty in the middle, so
    // saying the file is cropped costs no room at all: the alternative was a bar of its own above the log,
    // and a permanent strip is a high price for a state that is usually off. Clickable, unlike the update
    // notice beside it - it is the quickest way back to the whole file.
    // Hidden unless a crop is in force. Centred in the menu bar, which is otherwise empty in the middle, so
    // saying the file is cropped costs no room at all: the alternative was a bar of its own above the log,
    // and a permanent strip is a high price for a state that is usually off.
    //
    // Drawn as a link, because it is one: it takes the pointer's hand cursor and underlines under it, which
    // is what says "this can be clicked" without spending a sentence of the menu bar on saying so. The
    // trailing X says what clicking it does. The keystroke lives on the tip, where it is looked for once.
    private readonly ToolStripLabel _cropLabel = new()
    {
        Visible = false,
        Name = "menu.crop",
        Overflow = ToolStripItemOverflow.Never,
        AutoSize = true,
        IsLink = true,
        LinkBehavior = LinkBehavior.HoverUnderline,
        // A quiet slate rather than the default link blue, which shouts across a menu bar and reads as a web
        // page. Dark enough to sit against the chrome, coloured enough to say it is not just a label.
        LinkColor = Color.FromArgb(0x1F, 0x4E, 0x79),
        ActiveLinkColor = Color.FromArgb(0x0F, 0x2E, 0x4C),
    };
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Continuous, Visible = false, AutoSize = false, Width = 120 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 33 };
    private HangWatchdog? _watchdog;

    // The crop most recently applied, kept after it is hidden so that it can be put back without the lines
    // being picked out again. Never saved: a line number means nothing against a different file.
    private (long From, long ToExclusive)? _lastCrop;
    // What was chosen when that crop was taken. Taking a crop clears the selection - the lines were only ever
    // the way of naming the stretch, and leaving them all lit afterwards says nothing - so hiding the crop has
    // to be able to hand it back. Held with the version it was left at: the moment the reader chooses
    // anything themselves the claim lapses, and from then on the crop does not touch the selection at all.
    private LineGridControl.SelectionState? _cropSelection;
    private int _cropSelectionVersion = -1;
    private ToolStripMenuItem _miCrop = null!, _miUncrop = null!;

    private ToolStripMenuItem _miFilteredMode = null!, _miLineNumbers = null!, _miMarkers = null!;
    private ToolStripMenuItem _miPresets = null!, _miMatchMap = null!, _miWordWrap = null!, _miFilterTips = null!;
    private ToolStripMenuItem _miElapsed = null!, _miElapsedGutter = null!, _miElapsedStatus = null!, _miNoClock = null!;
    private ToolStripMenuItem _miMeasuredFrom = null!, _miNextOrigin = null!, _miSetReference = null!;
    private ToolStripMenuItem _miGoToReference = null!, _miClearReference = null!;
    private ToolStripMenuItem _miColumns = null!, _miLayoutColumns = null!, _miLayoutInline = null!, _miFitColumns = null!;
    private ToolStripMenuItem _miEncoding = null!;
    private ToolStripMenuItem _recentFilesMenu = null!, _recentFilterFilesMenu = null!;

    /// <summary>The encoding the reader chose for the file that is open, or null while it is being worked
    /// out from the file itself. Kept so that reloading does not quietly go back to guessing.</summary>
    private Encoding? _forcedEncoding;

    private FindBar _findBar = null!;
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
    private bool _tallyHiding, _tallySwept, _tallySettled;
    private int _activitySlot, _progressSlot, _baseActivitySlot;
    private int _elapsedSlot;
    private Font? _elapsedSlotFont;
    private bool _inStatusLayout;
    private (string Path, int Width) _shownSrc, _shownFilter;
    private int _treePanel = 2; // which split panel holds the filter tree (for show/hide)
    private bool _snapping;     // guards the divider being set from inside its own moved handler
    private bool _layoutSettled;// false until OnLoad has put the panes where the settings say they belong
    private bool _arranging;    // true while the app is moving the divider itself, rather than the user

    internal LineGridControl GridForTesting => _grid;
    internal SplitContainer SplitForTesting => _split;
    internal CascadeDocument DocForTesting => _doc;
    internal FilterTreeControl FilterTreeForTesting => _filterTree;
    internal string StatusForTesting => string.Join(" | ", _status.Items.OfType<ToolStripStatusLabel>().Select(l => l.Text));

    internal bool CropLabelVisibleForTesting => _cropLabel.Visible;

    internal bool CropToSelectionEnabledForTesting { get { SyncCropCommands(); return _miCrop.Enabled; } }

    /// <summary>Opens and closes the View menu, which is what settles the state of the items in it.</summary>
    internal void OpenViewMenuForTesting()
    {
        if (MainMenuStrip?.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "&View") is not { } view)
            return;
        view.ShowDropDown();
        view.HideDropDown();
    }

    internal string CropLabelTextForTesting => _cropLabel.Text ?? "";

    /// <summary>How far the crop chip's middle is from the middle of the menu bar, in pixels. The margin that
    /// centres it is worked out from where it landed, so the check is that it really did land there.</summary>
    internal int CropLabelCentreOffsetForTesting =>
        MainMenuStrip is { } menu && _cropLabel.Visible
            ? _cropLabel.Bounds.Left + _cropLabel.Width / 2 - menu.ClientSize.Width / 2
            : 0;

    /// <summary>Clicks a menu item by the path a user would read, so a check drives the same wiring rather
    /// than the method behind it. Each drop-down along the way is OPENED, because that is the only way a
    /// reader can reach the item - and it is where a menu settles what it is currently able to offer.
    /// </summary>
    internal bool ClickMenuForTesting(params string[] path)
    {
        ToolStripItemCollection? items = MainMenuStrip?.Items;
        ToolStripMenuItem? found = null;
        foreach (string want in path)
        {
            if (items is null) return false;
            if (found is not null) Open(found);
            found = items.OfType<ToolStripMenuItem>()
                         .FirstOrDefault(i => (i.Text ?? "").Replace("&", "").TrimEnd('.', '\u2026') == want);
            if (found is null) return false;
            items = found.DropDownItems;
        }
        if (found is null || !found.Enabled) return false;
        found.PerformClick();
        return true;

        static void Open(ToolStripDropDownItem item)
            => typeof(ToolStripDropDownItem).GetMethod("OnDropDownShow",
                   System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
               .Invoke(item, [EventArgs.Empty]);
    }

    /// <summary>Set by the headless screenshot harness: never prompt to save filters when closing. There is
    /// no user present to answer, so the modal prompt would block the render indefinitely.</summary>
    internal ProgressBar? StatusProgressForTesting => _progress.Control as ProgressBar;

    internal int SplitterDistanceForTesting => _split.SplitterDistance;
    internal bool FilterListVisibleForTesting => FilterListVisible;
    internal bool FilterListIsFirstPanelForTesting => _treePanel == 1;
    internal int RowPitchForTesting => _grid.RowPitch;
    internal int FindBarHeightForTesting => _findBar.Height;
    internal bool FindBarIsOpenForTesting => _findBar.Visible;
    internal bool FindBarRedrawsInOneGoForTesting => _findBar.MessageRedrawsInOneGoForTesting;
    internal FindBar FindBarForTesting => _findBar;
    internal FilterPresetsControl PresetsForTesting => _presets;
    /// <summary>Drives the form's own shortcut handling, which is where Tab and Escape are decided - and,
    /// through the form, the menu's. The message has to carry a window for that second part: the shortcut
    /// manager starts from the control the key arrived at, and a message from nowhere is not offered to any
    /// menu item at all.</summary>
    internal bool PressCmdKeyForTesting(Keys keys)
    {
        Message m = default;
        m.HWnd = Handle;
        return ProcessCmdKey(ref m, keys);
    }
    internal string FocusedAreaForTesting =>
        _findBar.ContainsFocus ? "find bar"
        : _filterTree.SearchBarHasFocus ? "filter search"
        : _filterTree.ListHasFocus ? "filter list"
        : _grid.Focused ? "log"
        : ActiveControl?.Name is { Length: > 0 } n ? n : ActiveControl?.GetType().Name ?? "(nothing)";
    internal void CloseFindForTesting() => CloseFind();

    /// <summary>Opens a log the way File ▸ Open does, so a check exercises the whole path (including what
    /// opening another file does to the chosen encoding).</summary>
    internal void OpenForTesting(string path) => OpenFile(path, null);

    /// <summary>What the Encoding menu says right now, one entry per line: the item's name, Auto-detect's
    /// hint in brackets, <c>*</c> for the one in effect. Opens the drop-down the way Windows does, so the
    /// check reads what a reader would see rather than the field behind it.</summary>
    internal string EncodingMenuForTesting()
    {
        typeof(ToolStripDropDownItem).GetMethod("OnDropDownShow",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_miEncoding, [EventArgs.Empty]);
        return string.Join("\n", _miEncoding.DropDownItems.OfType<ToolStripMenuItem>().Select(i =>
            (i.Text ?? "").Replace("&", "", StringComparison.Ordinal)
            + (i.ShortcutKeyDisplayString is { Length: > 0 } hint ? $" [{hint}]" : "")
            + (i.Checked ? " *" : "")
            + (i.Enabled ? "" : " (unavailable)")));
    }

    internal void SetStatusProgressForTesting(double fraction)
    {
        _progress.Visible = true;
        SetProgress(fraction);
    }

    /// <summary>What the status bar says, field by field, with a <c>|</c> in front of each one that carries a
    /// divider down its left edge - which is how the bar reads as a row of separate measurements rather than
    /// one long stretch of text.</summary>
    internal string StatusDividersForTesting => string.Join(" ", _status.Items.OfType<ToolStripStatusLabel>()
        .Where(l => l.Visible)
        .Select(l => (l.BorderSides.HasFlag(ToolStripStatusLabelBorderSides.Left) ? "|" : "")
                     + (l.Name?.StartsWith("stat.", StringComparison.Ordinal) == true ? l.Name[5..] : "path")));

    /// <summary>What the status bar's elapsed slot is saying, or nothing at all when it is not there. Read
    /// through its own visibility rather than off the whole bar: a hidden label keeps whatever text it last
    /// had, and "the slot is gone" is exactly what has to be checkable.</summary>
    internal string ElapsedSlotForTesting => _elapsedLabel.Visible ? _elapsedLabel.Text ?? "" : "";

    /// <summary>How the status bar has divided itself up: the fixed boxes, the room that leaves the two
    /// springing paths, and what each of them could show in it. Everything on the bar competes for one row,
    /// so "which of these took the paths' room" is the only way to answer a complaint about them.</summary>
    internal string StatusLayoutForTesting
    {
        get
        {
            long magnitude = MetricMagnitude();
            var parts = _status.Items.OfType<ToolStripStatusLabel>()
                .Where(l => l.Visible)
                .Select(l => $"{(l.Name?.StartsWith("stat.", StringComparison.Ordinal) == true ? l.Name[5..] : "path")}={l.Width}");
            return $"bar={_status.Width} metrics={TotalMetricWidth(magnitude)} activity={_activitySlot} "
                 + $"elapsed={ElapsedSlotWidth()} magnitude={magnitude:N0} pathroom={PathRoom(magnitude)} "
                 + $"floor={Dpi(300)} | {string.Join(" ", parts)}";
        }
    }

    /// <summary>What the Elapsed Time menu offers right now, one entry a line, opened the way Windows opens
    /// it - so a check reads what a reader would see rather than the fields behind it.</summary>
    internal string ElapsedMenuForTesting()
    {
        typeof(ToolStripDropDownItem).GetMethod("OnDropDownShow",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_miElapsed, [EventArgs.Empty]);
        return string.Join("\n", _miElapsed.DropDownItems.OfType<ToolStripMenuItem>()
            .Where(i => i.Available)
            .Select(i => (i.Text ?? "").Replace("&", "", StringComparison.Ordinal)
                         + (i.Checked ? " *" : "")
                         + (i.Enabled ? "" : " (unavailable)")));
    }

    /// <summary>How wide the Elapsed Time drop-down comes out, against the WORDING in it. A menu item is
    /// stretched to the width of the drop-down, so asking an item for its preferred size only hands the
    /// same figure back - the text has to be measured directly to tell "the wording is long" from
    /// "something not on show is still taking room".</summary>
    internal string ElapsedMenuWidthForTesting()
    {
        SyncElapsedMenu();
        _miElapsed.DropDown.PerformLayout();
        static int Words(ToolStripMenuItem i)
            => TextRenderer.MeasureText(i.Text ?? "", i.Font).Width
             + TextRenderer.MeasureText(i.ShortcutKeyDisplayString ?? "", i.Font).Width;
        var all = _miElapsed.DropDownItems.OfType<ToolStripMenuItem>().ToList();
        int widest = all.Where(i => i.Available).Select(Words).DefaultIfEmpty(0).Max();
        return $"drop={_miElapsed.DropDown.Width} widest={widest} | "
             + string.Join(" ", all.Select(i => $"\u201c{(i.Text ?? "").Replace("&", "", StringComparison.Ordinal)}\u201d"
                                              + $"={Words(i)}{(i.Available ? "" : "(off)")}"));
    }

    /// <summary>The elapsed entries that advertise a key, each with the key it shows. They are not
    /// registered as ShortcutKeys, so nothing but the displayed string tells a reader they exist - and a
    /// menu item paints EITHER a shortcut or a submenu arrow, so an item with children can advertise
    /// nothing at all.</summary>
    internal string ElapsedMenuKeysForTesting()
        => string.Join("|", new[] { _miElapsedGutter, _miElapsedStatus, _miNextOrigin, _miSetReference, _miGoToReference }
            .Select(i => $"{i.Text}={i.ShortcutKeyDisplayString}"
                       + (i.HasDropDownItems ? " (ARROW HIDES IT)" : "")));

    /// <summary>Whether the status bar will show the reasons written on it at all. A StatusStrip turns item
    /// tooltips OFF by default, which quietly threw away every explanation on it.</summary>
    internal string StatusTipsForTesting
        => $"shown={_status.ShowItemToolTips} elapsed=\u201c{_elapsedLabel.ToolTipText}\u201d";

    internal bool ShowElapsedGutterForTesting => _settings.ShowElapsedGutter;

    /// <summary>What the column is measuring from and where it is measuring from, in one line - the two
    /// have to agree, and a check reading only the setting would not notice when they stopped.</summary>
    internal string ElapsedOriginForTesting
        => $"{EffectiveOrigin} ref={_doc.ReferenceLine} said=\u201c{OriginPrefix.TrimEnd(' ', ':')}\u201d";
    /// <summary>What "Measured From" offers, one entry a line, with the one in force marked.</summary>
    internal string MeasuredFromMenuForTesting()
    {
        SyncElapsedMenu();
        return string.Join("\n", _miMeasuredFrom.DropDownItems.OfType<ToolStripMenuItem>()
            .Select(i => (i.Text ?? "").Replace("&", "", StringComparison.Ordinal)
                         + (i.Checked ? " *" : "")
                         + (i.Enabled ? "" : " (unavailable)")));
    }

    /// <summary>The short-lived message and the reason behind it, as the status bar is carrying them.</summary>
    internal string FindMessageForTesting => $"{_findMsg} \u2014 {_findMsgDetail}";

    internal void ClearReferenceForTesting() => ClearReference();

    internal bool NoSavePrompt;    private bool _offScreen;
    // Harness only: shows the update notice without an update actually being pending.
    internal string? UpdateNoticeOverride;

    /// <summary>Harness only: whether indexing or filtering is still running, so a render can wait for a
    /// settled window instead of a flat timeout.</summary>
    internal bool IsBusyForHarness => _doc.IsBusy;

    /// <summary>Null when updating is switched off. Only ever read here - the swap happens in Program after
    /// the message loop ends.</summary>
    private readonly UpdateService? _updater;

    public MainForm(AppSettings settings, MachineState state, string[] args, UpdateService? updater = null)
    {
        Automation.Suppress(this);
        _settings = settings;
        _state = state;
        _updater = updater;
        Text = "Cascade";
        OpenMaximised();
        Icon = LoadAppIcon();
        PlaceOffScreenIfAsked();

        BuildMenu();
        BuildStatusBar();
        BuildFindBar();

        // The bar goes INSIDE the log view, above its text but inside its scrollbar and minimap, so opening
        // it shortens the text and leaves those two standing their full height instead of shoving them down.
        _grid.HostAtTop(_findBar);
        _split.Panel1.Controls.Add(_grid);
        _filterPane.Panel1.Controls.Add(_filterTree);
        _filterPane.Panel2.Controls.Add(_presets);
        _split.Panel2.Controls.Add(_filterPane);
        Controls.Add(_split);
        Controls.Add(_status);
        _split.BringToFront();
        _split.SplitterMoved += (_, _) => { SnapSplitter(); RememberFilterListSize(); };
        _grid.ChromeChanged += SnapSplitter;

        _grid.Attach(_doc, _settings);
        _filterTree.Attach(_doc);
        _presets.Attach(_doc);
        _filterTree.SetSettings(_settings);

        _doc.Updated += () => _pendingRefresh = true;
        // A held drag or a held key never lets the message queue empty, and a repaint only arrives when it
        // does - so anything that has to keep up with the gesture is pushed out rather than waited for.
        // A note about a match being out of sight answers for the line the search landed on, so moving to
        // another line by hand retires it; the next search will say so again if it still applies.
        _grid.SelectionChanged += () => { _hiddenMatch = ""; SyncCropCommands(); UpdateStatus(); _status.Update(); };
        _grid.NewFilterRequested += NewFilterFromDoubleClick;
        _grid.ZoomChanged += () => { UpdateStatus(); _status.Update(); SaveSettingsSoon(); _findBar.SnapHeightTo(_grid.RowPitch); SnapSplitter(); };
        _filterTree.FiltersChanged += OnFiltersChanged;
        _filterTree.BeforeFiltersEdited += label => _history.Begin(label, _doc.Filters);
        _presets.PresetsApplied += () => { _filterTree.RefreshCheckStates(); OnFiltersChanged(); };
        _presets.PresetsEdited += () => { _filtersDirty = true; UpdateTitle(); };
        // Columns are saved in the filter file, so a width dragged in the header is an unsaved change just
        // as an edited filter is. Only on the way from clean to dirty: a drag reports every step of itself,
        // and rewriting the title bar sixty times a second is not free.
        _grid.ColumnsChanged += () =>
        {
            // Whatever was hidden or carried about has changed, so a note about a match being out of sight
            // no longer answers for anything.
            _hiddenMatch = "";
            if (!_filtersDirty) { _filtersDirty = true; UpdateTitle(); }
        };
        _grid.ColumnSettingsRequested += ShowColumns;
        _filterTree.EditRequested += EditFilter;
        // Every way of asking for a filter goes through the one command, so a key advertised in the list's
        // own menu does exactly what the same key does in the log: seed from the selection, offer the places.
        _filterTree.AddRequested += NewFilter;
        _filterTree.FindFilterRequested += FindFilterMatch;
        _filterTree.NoFilterMatch += q => NoMoreMatches("No more filters", $"No more filters matching {Quote(q)}");
        _grid.NoMoreMarkers += i => NoMoreMatches($"No more marker {i + 1}");
        _grid.CopyTruncated += (copied, selected) => ShowFindMessage(
            $"Copied {copied:N0} of {selected:N0} lines",
            $"The clipboard will not take the whole selection. Use File \u25b8 Save Current Lines\u2026 for all {selected:N0}.");

        // A log dragged in from Explorer replaces the one on screen and keeps the filters. Every area that
        // covers the window has to accept the drop for itself; the filter pane answers for its own, since
        // it is already a drop target for reordering filters.
        AcceptFileDrops(this);
        AcceptFileDrops(_grid);
        _filterTree.FilesDropped += OpenDroppedFiles;

        _refreshTimer.Tick += (_, _) =>
        {
            _watchdog?.Beat();
            if (_watchdog?.TakeReport() is { } dump)
                ShowFindMessage($"Hang recorded: {dump}", Path.Combine(HangWatchdog.Folder, dump));
            if (_pendingRefresh) { _pendingRefresh = false; _grid.RefreshView(); _grid.InvalidateMatchMap(); _filterTree.RefreshCounts(); }
            else if (_doc.IsBusy) _filterTree.RefreshCounts();
            if (_anchorActive && !_doc.IsBusy) { _grid.RefreshView(); _grid.RetireViewAnchor(); _anchorActive = false; }
            _doc.DropRememberedViews();
            UpdateStatusIfChanged();
            FlushConfig();
        };
        _refreshTimer.Start();
        SyncHangWatchdog();

        Shown += (_, _) => ProcessArgs(args);
        FormClosing += OnClosing;
        SyncUndoMenu();
        UpdateStatus();
    }

    /// <summary>Comes up maximised, but sized first, and in that order.
    ///
    /// <para>WinForms keeps back any bounds set while the state is Maximized and forces them onto the window
    /// the moment it stops being maximised - <c>Form.SetBoundsCore</c> stashes them, <c>Form.WndProc</c>
    /// hands them back through <c>RestoreWindowBoundsIfNecessary</c> on the next WM_WINDOWPOSCHANGED. Aero
    /// Snap leaves the maximised state as the first half of what it does, so Win+Left would take Windows'
    /// half-screen rectangle and then be shrunk, on the spot, to whatever was last set here: MinimumSize,
    /// left sitting in the corner the snap had just moved it to. Setting the size while the state is still
    /// Normal leaves nothing pending, and the snapped rectangle stands - as it does for every other app.</para>
    ///
    /// <para>The size is also what Win+Down and a double-clicked title bar restore to, so it is worth a
    /// window that can be worked in rather than the smallest one allowed. Measured off the primary screen,
    /// which is where a window that asks for no position of its own is put, and floored by the MinimumSize
    /// set right after it - that setter grows the size to meet it.</para></summary>
    private void OpenMaximised()
    {
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Size = new Size(work.Width * 3 / 4, work.Height * 3 / 4);
        MinimumSize = new Size(700, 400);
        WindowState = FormWindowState.Maximized;
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
        // The window comes back the way it was left: the list on the edge it was docked to, given the share
        // of the window it was dragged to, and out of sight if that is where it was put. The divider then
        // rounds up to a whole number of lines. The defaults are the layout the app has always opened with,
        // so a machine that has never rearranged anything sees no change at all.
        ApplyFilterDock(_settings.FilterListDock);
        ApplyFilterListSize();
        SetFilterListVisible(_settings.ShowFilterList);
        LayoutPresetPane();
        _grid.SetMatchMapVisible(_settings.ShowMatchMap);
        // Only now are the panes the size they are going to keep, so only now is a divider worth recording.
        _layoutSettled = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Tab inside either bar walks that bar's OWN controls and wraps at the ends. Letting the form have
        // it instead tabs out of the bar into whatever is next in the window - the presets list, as it
        // happens - with no way back in. A bar is left through Escape, not through Tab.
        if (keyData is Keys.Tab or (Keys.Shift | Keys.Tab))
        {
            bool forward = keyData == Keys.Tab;
            if (_findBar.ContainsFocus) { _findBar.MoveFocusWithin(forward); return true; }
            if (_filterTree.SearchBarHasFocus) { _filterTree.MoveSearchFocusWithin(forward); return true; }
            CycleFocus(forward);
            return true;
        }

        // ESCAPE, IN ORDER. Three things answer to it, and they are ranked by how loudly each is asking:
        //  1. A search actually running. The status bar is at that moment telling the user "Esc to stop",
        //     so it gets the key wherever they happen to be standing.
        //  2. The filter search bar, but only from inside the filter pane. The two panes are disjoint, so
        //     scoping this one to focus removes the conflict outright rather than ranking two unrelated
        //     things against each other - and it is the nearest state to hand when you are in that pane.
        //  3. The find bar, which is the log pane's equivalent and the fallback everywhere else.
        if (keyData == Keys.Escape && _findBusy) { _doc.CancelFind(); return true; }
        if (keyData == Keys.Escape && _saveCts is { } saving) { saving.Cancel(); return true; }
        if (keyData == Keys.Escape && _filterTree.SearchOpen && _filterTree.ContainsFocus)
        {
            _filterTree.HideSearch();
            return true;
        }
        // ...and once nothing is running, Esc puts the whole thing away: bar closed, highlights off, counts
        // with them. It has to work while typing the term too, which is why the general "not in a text box"
        // guard only applies outside the bar.
        if (keyData == Keys.Escape && (_findBar.Visible || _lastQuery is not null)
            && (_findBar.ContainsFocus || !IsTextInputFocused())) { CloseFind(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.L)) { ToggleFilterList(); return true; }
        if (keyData == SwitchLayoutKey && SwitchLayout()) return true;
        // Handled here rather than registered on the menu items, because whether they may run depends on
        // the log having a clock - and the items only learn that when the View menu is opened.
        if (keyData == ElapsedGutterKey) return ToggleElapsed(margin: true);
        if (keyData == ElapsedStatusKey) return ToggleElapsed(margin: false);
        if (keyData == SetReferenceKey) return SetReferenceHere();
        if (keyData == CycleOriginKey) return CycleOrigin();
        if (keyData == GoToReferenceKey) return GoToReference();
        // Before anything the menu claims: a shortcut registered on a menu item is dispatched by the base
        // call below, over the head of whatever has the focus.
        if (EditFocusedText(keyData)) return true;
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
        { Checked = _settings.ShowLineNumbers, ShortcutKeys = Keys.Control | Keys.L };
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
        _miWordWrap = new ToolStripMenuItem("&Word Wrap", null, (_, _) =>
        {
            _settings.WordWrap = !_settings.WordWrap;
            _miWordWrap.Checked = _settings.WordWrap;
            _grid.RefreshView();
            SaveSettingsSoon();
        })
        { Checked = _settings.WordWrap, ShortcutKeys = Keys.Alt | Keys.Z, ShortcutKeyDisplayString = "Alt+Z" };
        view.DropDownItems.Add(_miWordWrap);
        // Wrapping and the Columns layout lay text out in ways that would tear each other apart, so the item
        // is greyed rather than left to be ticked and quietly ignored. Inline is only a shorter line, and
        // wraps like any other.
        view.DropDownOpening += (_, _) =>
        {
            _miWordWrap.Enabled = !(_doc.Columns.Active && _doc.Columns.Layout == FieldLayout.Columns);
            _miFitColumns.Enabled = _doc.Columns.Active && _doc.Columns.Layout == FieldLayout.Columns;
            SyncColumnsMenu();
            SyncElapsedMenu();
        };
        _miFilterTips = new ToolStripMenuItem("Show Matching Filters on Ho&ver", null, (_, _) =>
        {
            _settings.ShowFilterTooltips = !_settings.ShowFilterTooltips;
            _miFilterTips.Checked = _settings.ShowFilterTooltips;
            SaveSettingsSoon();
        })
        { Checked = _settings.ShowFilterTooltips };
        view.DropDownItems.Add(_miFilterTips);
        view.DropDownItems.Add(BuildMarkersMenu());
        view.DropDownItems.Add(BuildElapsedMenu());
        // The two layouts are not separate commands sitting beside the switch - they are the two ways the
        // switch can be thrown, and reading them as a flat list of three left it unclear that turning the
        // middle one on turned the top one on too. Nested, the shape says it: one thing, with a choice
        // inside it. Clicking the parent still throws the switch, and each layout is still one click away.
        _miColumns = new CommandWithSubmenu("Split Li&nes Into Fields", (_, _) => ToggleColumns())
        { Checked = _doc.Columns.Enabled, ShortcutKeys = Keys.Control | Keys.Shift | Keys.C };
        view.DropDownItems.Add(_miColumns);

        _miLayoutColumns = new ToolStripMenuItem("Lay Out as &Columns", null, (_, _) => SetLayout(FieldLayout.Columns))
        { ToolTipText = "A table: every field gets a column, lined up under a header you can drag." };
        _miLayoutInline = new ToolStripMenuItem("La&y Out Inline", null, (_, _) => SetLayout(FieldLayout.Inline))
        { ToolTipText = "Each row stays a line, shortened by whatever you have hidden." };
        _miColumns.DropDownItems.Add(_miLayoutColumns);
        _miColumns.DropDownItems.Add(_miLayoutInline);
        _miColumns.DropDownItems.Add(new ToolStripSeparator());

        _miFitColumns = Mi("&Fit Columns to Window", (_, _) => _grid.FitColumnsToWindow());
        _miColumns.DropDownItems.Add(_miFitColumns);
        view.DropDownItems.Add(Mi("Field Settin&gs…", (_, _) => ShowColumns(), Keys.Control | Keys.Shift | Keys.D));
        view.DropDownItems.Add(new ToolStripSeparator());
        _miCrop = Mi("&Crop to Selection", (_, _) => CropToSelection(), Keys.Control | Keys.OemOpenBrackets, "Ctrl+[");
        _miCrop.ToolTipText = "Show only the selected lines, and treat the file as if it held nothing else.";
        _miUncrop = Mi("Hide or Re-appl&y Crop", (_, _) => ToggleCrop(), Keys.Control | Keys.OemCloseBrackets, "Ctrl+]");
        _miUncrop.ToolTipText = "Go back to the whole file, or return to the crop you last set.";
        view.DropDownItems.Add(_miCrop);
        view.DropDownItems.Add(_miUncrop);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Mi("Zoom &In", (_, _) => _grid.Zoom(10), Keys.Control | Keys.Oemplus, "Ctrl++"));
        view.DropDownItems.Add(Mi("Zoom &Out", (_, _) => _grid.Zoom(-10), Keys.Control | Keys.OemMinus, "Ctrl+-"));
        view.DropDownItems.Add(Mi("&Reset Zoom", (_, _) => _grid.ResetZoom(), Keys.Control | Keys.D0));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Mi("Focus &Text Area", (_, _) => FocusTextArea(), Keys.Control | Keys.Shift | Keys.T));
        view.DropDownItems.Add(Mi("Foc&us Filter List", (_, _) => FocusFilterList(), Keys.Control | Keys.Shift | Keys.F));
        view.DropDownItems.Add(Mi("Fin&d Filter", (_, _) => FocusFilterSearch(), Keys.Control | Keys.E));
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
        view.DropDownOpening += (_, _) => SyncCropMenu();

        var filters = new ToolStripMenuItem("Fi&lters");
        // The three places a new filter can go, each on the key that pre-picks it in the dialog. They all
        // seed the filter from whatever is picked out in the log, which is what Ctrl+N always did.
        filters.DropDownItems.Add(Mi("&Add Filter\u2026", (_, _) => NewFilter(NewFilterPlacement.Default), NewFilterKeys.Add));
        filters.DropDownItems.Add(Mi("Add Filter Abo&ve Selected\u2026", (_, _) => NewFilter(NewFilterPlacement.Above), NewFilterKeys.AddAbove));
        filters.DropDownItems.Add(Mi("Add &Child Filter\u2026", (_, _) => NewFilter(NewFilterPlacement.Child), NewFilterKeys.AddChild));
        var miEdit = Mi("&Edit Filter…", (_, _) => { if (_filterTree.SelectedFilter is { } f) EditFilter(f); });
        var miDuplicate = Mi("Duplica&te Filter", (_, _) => _filterTree.DuplicateSelected(), Keys.Control | Keys.D);
        var miRemove = Mi("&Remove Filter", (_, _) => _filterTree.RemoveSelected());
        filters.DropDownItems.Add(miEdit);
        filters.DropDownItems.Add(miDuplicate);
        filters.DropDownItems.Add(miRemove);
        filters.DropDownItems.Add(new ToolStripSeparator());
        // The keys themselves are handled by the filter tree (they only apply while it has focus), so these
        // just advertise them.
        filters.DropDownItems.Add(Hint("Move &Up", "Alt+Up", () => _filterTree.MoveSelected(Keys.Up)));
        filters.DropDownItems.Add(Hint("Move &Down", "Alt+Down", () => _filterTree.MoveSelected(Keys.Down)));
        filters.DropDownItems.Add(Hint("&Indent (nest under filter above)", "Alt+Right", () => _filterTree.MoveSelected(Keys.Right)));
        filters.DropDownItems.Add(Hint("&Outdent", "Alt+Left", () => _filterTree.MoveSelected(Keys.Left)));
        // What the commands above will act on. Several filters can be selected and scrolled out of sight,
        // so the menu says how many rather than leaving Remove to be found out about afterwards.
        filters.DropDownOpening += (_, _) =>
        {
            int n = _filterTree.SelectedCount;
            miEdit.Text = n > 1 ? $"&Edit Appearance of {n} Filters…" : "&Edit Filter…";
            miDuplicate.Text = n > 1 ? $"Duplica&te {n} Filters" : "Duplica&te Filter";
            miRemove.Text = n > 1 ? $"&Remove {n} Filters" : "&Remove Filter";
        };
        filters.DropDownItems.Add(new ToolStripSeparator());
        filters.DropDownItems.Add(Mi("Find &Next Match", (_, _) => FindSelectedFilterMatch(true), Keys.F4));
        // "Match" rather than "Previous": v now underlines the new filter placed above the selected one, and
        // one letter cannot mean two things in one menu.
        filters.DropDownItems.Add(Mi("Find Previous &Match", (_, _) => FindSelectedFilterMatch(false), Keys.Shift | Keys.F4));
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
        filters.DropDownItems.Add(Hint("&Find Filter", "Ctrl+E", FocusFilterSearch));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Mi("&About Cascade", (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, filters, help, _cropLabel, _updateLabel });
        menu.SizeChanged += (_, _) => CentreCropLabel();
        _cropLabel.Click += (_, _) => ToggleCrop();
        MainMenuStrip = menu;
        Controls.Add(menu);
        RefreshRecentMenus();
    }

    /// <summary>The preset commands, so everything the pane's context menu offers is reachable from the
    /// keyboard too.</summary>
    private ToolStripMenuItem BuildPresetsMenu()
    {
        var m = new ToolStripMenuItem("&Presets");
        m.DropDownItems.Add(Mi("Apply &Only This Preset", (_, _) => _presets.ApplyOnlySelected()));
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

    /// <summary>Ticks whichever marker mode is actually in effect. Importing a settings file can change the
    /// same setting, so the menu has to re-read it rather than assume it still owns it.</summary>
    private void SyncMarkersMenu()
    {
        foreach (ToolStripMenuItem item in _miMarkers.DropDownItems)
            item.Checked = (MarkerVisibilityMode)item.Tag! == _settings.MarkerVisibility;
    }

    /// <summary>Where the two elapsed-time displays are turned on and off, and - when the log has no clock
    /// anyone could find - where that is said.
    ///
    /// <para>The reason is a line of its own rather than a tip on a greyed item, because a tip has to be
    /// hunted for and this is exactly the moment somebody wants telling. It says where to go rather than
    /// offering to take them: Field Settings is four items further down this same menu, and a second door
    /// into one dialog is two things to keep in step.</para></summary>
    private ToolStripMenuItem BuildElapsedMenu()
    {
        _miElapsed = new ToolStripMenuItem("Elap&sed Time");

        _miElapsedGutter = new ToolStripMenuItem("In the &Margin", null, (_, _) => ToggleElapsed(margin: true))
        { ShortcutKeyDisplayString = "Ctrl+Shift+M" };
        _miElapsedStatus = new ToolStripMenuItem("In the Status &Bar", null, (_, _) => ToggleElapsed(margin: false))
        { ShortcutKeyDisplayString = "Ctrl+Shift+B" };
        _miNoClock = new ToolStripMenuItem("No timestamp field \u2014 set one in Field Settings") { Enabled = false };

        _miMeasuredFrom = new ToolStripMenuItem("Measured F&rom");
        foreach (var (origin, text) in Origins)
            _miMeasuredFrom.DropDownItems.Add(
                new ToolStripMenuItem(text, null, (_, _) => MeasureFrom(origin)) { Tag = origin });
        // A separate entry rather than a key on the submenu above it: a menu item paints EITHER a shortcut
        // or a drop-down arrow in that column, never both, so a key put on "Measured From" is advertised
        // nowhere at all - which a picture of the open menu is the only way to notice.
        _miNextOrigin = new ToolStripMenuItem("S&witch to the Next", null, (_, _) => CycleOrigin())
        { ShortcutKeyDisplayString = "Ctrl+Shift+R" };
        _miSetReference = new ToolStripMenuItem("Set the Reference &Here", null, (_, _) => SetReferenceHere())
        { ShortcutKeyDisplayString = "Ctrl+R" };
        _miGoToReference = new ToolStripMenuItem("&Go to the Reference", null, (_, _) => GoToReference())
        { ShortcutKeyDisplayString = "Ctrl+Shift+G" };
        _miClearReference = new ToolStripMenuItem("&Clear the Reference", null, (_, _) => ClearReference());

        _miElapsed.DropDownItems.Add(_miElapsedGutter);
        _miElapsed.DropDownItems.Add(_miElapsedStatus);
        _miElapsed.DropDownItems.Add(new ToolStripSeparator());
        _miElapsed.DropDownItems.Add(_miMeasuredFrom);
        _miElapsed.DropDownItems.Add(_miNextOrigin);
        _miElapsed.DropDownItems.Add(_miSetReference);
        _miElapsed.DropDownItems.Add(_miGoToReference);
        _miElapsed.DropDownItems.Add(_miClearReference);
        _miElapsed.DropDownItems.Add(_miNoClock);
        _miElapsed.DropDownOpening += (_, _) => SyncElapsedMenu();
        SyncElapsedMenu();
        return _miElapsed;
    }

    /// <summary>The three origins in the order the cycling key walks them, with the wording the menu shows.
    /// One list, so the menu and the key can never come to offer different things.</summary>
    private static readonly (ElapsedOrigin Origin, string Text)[] Origins =
    [
        (ElapsedOrigin.PreviousShown, "the &Previous Line Shown"),
        (ElapsedOrigin.FileStart,     "the &Start of the File"),
        (ElapsedOrigin.Reference,     "the &Reference Line"),
    ];

    // Each key is the letter its own item underlines - "In the &Margin", "In the Status &Bar" - so there is
    // one letter to learn per entry rather than a shortcut vocabulary of its own. Ctrl+Shift+S would have
    // matched "Status" better still, but Ctrl+S here saves filters and every app in the world reads
    // Ctrl+Shift+S as Save As. Ctrl+R sets the reference and Ctrl+Shift+R steps through what is measured
    // from - one letter for one idea, the pairing Ctrl+Shift+X already uses for switching the field layout.
    // Going to the reference is the same pairing on the key that already means going somewhere: Ctrl+G goes
    // to a line you name, Ctrl+Shift+G to the one already named.
    private const Keys ElapsedGutterKey = Keys.Control | Keys.Shift | Keys.M;
    private const Keys ElapsedStatusKey = Keys.Control | Keys.Shift | Keys.B;
    private const Keys SetReferenceKey = Keys.Control | Keys.R;
    private const Keys CycleOriginKey = Keys.Control | Keys.Shift | Keys.R;
    private const Keys GoToReferenceKey = Keys.Control | Keys.Shift | Keys.G;

    /// <summary>Names the caret's line as the one everything is measured from, and starts measuring from
    /// it: setting an origin and leaving the column showing something else would look like the key had done
    /// nothing at all.</summary>
    private bool SetReferenceHere()
    {
        if (_doc.Clock is null) return false;
        long line = _grid.SelectionBounds(out long first, out _) ? first : -1;
        if (line < 0 || !_doc.TrySetReference(line))
            NoMoreMatches(line < 0 ? "Select a line first" : "No time on that line",
                          line < 0
                              ? "Select a line to measure from."
                              : $"Line {line + 1:N0} carries no time of its own, so nothing can be measured from it.");
        else
            MeasureFrom(ElapsedOrigin.Reference);
        return true;
    }

    private void ClearReference()
    {
        _doc.ClearReference();
        if (_settings.ElapsedMeasuredFrom == ElapsedOrigin.Reference) MeasureFrom(ElapsedOrigin.PreviousShown);
        else RefreshElapsed();
    }

    /// <summary>Takes the view back to the line everything is being measured from.
    ///
    /// <para>A reference is set on a line worth coming back to and then scrolled away from - that is what it
    /// is FOR - and in a file of seventy million lines there was no way back to it but to have remembered
    /// the number. The one record of where it was sat in a tooltip on the entry that throws it away.</para>
    ///
    /// <para>Filtered out, it lands on the nearest line still shown, the way Go To Line does - and says so,
    /// because a jump that quietly arrives somewhere else is worse than one that explains itself.</para>
    /// </summary>
    private bool GoToReference()
    {
        if (_doc.Clock is null) return false;
        long line = _doc.ReferenceLine;
        if (line < 0)
        {
            NoMoreMatches("No reference line yet",
                          "Select a line and press Ctrl+R to measure everything from it.");
            return true;
        }
        bool shown = _doc.RowForLine(line) >= 0;
        GoToLine(line + 1);
        if (!shown)
            ShowFindMessage($"Line {line + 1:N0} is not being shown",
                            $"The reference is filtered out, so this is the nearest line to it that is shown.");
        return true;
    }

    /// <summary>Steps to the next origin there is anything to measure from. With no reference named there
    /// is nothing to see in that mode, so the key passes over it rather than stopping on an empty column.
    /// </summary>
    private bool CycleOrigin()
    {
        if (_doc.Clock is null) return false;
        // From what the column is SHOWING, not from what the setting remembers. Opening a file drops the
        // reference without touching the setting, so a cycle starting from the setting spent its first
        // press moving off an origin that was already resolving to somewhere else - a key that did nothing.
        int at = Array.FindIndex(Origins, o => o.Origin == EffectiveOrigin);
        for (int step = 1; step <= Origins.Length; step++)
        {
            var next = Origins[(at + step + Origins.Length) % Origins.Length].Origin;
            if (next == ElapsedOrigin.Reference && _doc.ReferenceLine < 0) continue;
            MeasureFrom(next);
            break;
        }
        return true;
    }

    private void MeasureFrom(ElapsedOrigin origin)
    {
        _settings.ElapsedMeasuredFrom = origin;
        SaveSettingsSoon();
        RefreshElapsed();
    }

    private void RefreshElapsed()
    {
        _grid.RefreshView();
        SyncElapsedMenu();
        UpdateStatus();
    }

    /// <summary>Turns one of the two displays on or off, and answers whether it did - so the key can fall
    /// through to whatever else wants it on a log with no clock, rather than appearing to do nothing.
    /// </summary>
    private bool ToggleElapsed(bool margin)
    {
        if (_doc.Clock is null) return false;
        if (margin)
        {
            _settings.ShowElapsedGutter = !_settings.ShowElapsedGutter;
            _grid.RefreshView();
        }
        else
        {
            _settings.ShowElapsedInStatusBar = !_settings.ShowElapsedInStatusBar;
            UpdateStatus();
        }
        SaveSettingsSoon();
        return true;
    }

    private void SyncElapsedMenu()
    {
        bool have = _doc.Clock is not null;
        _miElapsedGutter.Enabled = _miElapsedStatus.Enabled = _miMeasuredFrom.Enabled = have;
        _miNextOrigin.Enabled = have;
        _miElapsedGutter.Checked = _settings.ShowElapsedGutter;
        _miElapsedStatus.Checked = _settings.ShowElapsedInStatusBar;

        bool named = _doc.ReferenceLine >= 0;
        _miSetReference.Enabled = have;
        _miGoToReference.Enabled = named;
        _miClearReference.Enabled = named;
        // Wording that does not move: an item renaming itself to carry the line number cannot be found by
        // name afterwards, by a check or by anything else. Where the reference IS belongs on the status
        // bar, which says so beside the figure it is producing - and on the tip of the two entries that act
        // on it, which is where the question is asked.
        _miGoToReference.ToolTipText = _miClearReference.ToolTipText =
            named ? $"Line {_doc.ReferenceLine + 1:N0}" : "";
        foreach (ToolStripMenuItem item in _miMeasuredFrom.DropDownItems)
        {
            var origin = (ElapsedOrigin)item.Tag!;
            item.Checked = origin == EffectiveOrigin;
            item.Enabled = origin != ElapsedOrigin.Reference || named;
        }

        // TAKEN OUT of the list rather than hidden in it. MEASURED: a ToolStripDropDown that has once been
        // laid out around an item does not give the width back when that item is hidden, so a sentence three
        // times the length of the two entries left the menu that wide for the rest of the session.
        bool listed = _miElapsed.DropDownItems.Contains(_miNoClock);
        if (have && listed) _miElapsed.DropDownItems.Remove(_miNoClock);
        else if (!have && !listed) _miElapsed.DropDownItems.Add(_miNoClock);
    }

    /// <summary>What the figures are really measured from, resolved by the document so that the margin and
    /// the status bar can never come to different answers.</summary>
    private ElapsedOrigin EffectiveOrigin => _doc.Resolve(_settings.ElapsedMeasuredFrom);

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

    /// <summary>How the bytes on disk are turned into text. Every item is ticked or not, so the menu says
    /// which one is in effect - and "Auto-detect" names what it worked out, since that is the answer a
    /// reader actually wants and no other item can carry it.</summary>
    private ToolStripMenuItem BuildEncodingMenu()
    {
        _miEncoding = new ToolStripMenuItem("&Encoding");
        _miEncoding.DropDownItems.Add(new ToolStripMenuItem("&Auto-detect", null, (_, _) => ReopenWithEncoding(null)));

        var offered = new HashSet<int>();
        void Item(string text, int codePage)
        {
            // The system default is Windows-1252 on most machines, and two items for one encoding would
            // both be ticked by the same choice - which reads as the menu being confused about itself.
            if (!offered.Add(codePage)) return;
            var enc = CodePage(codePage);
            _miEncoding.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => { if (enc is not null) ReopenWithEncoding(enc); })
            {
                Tag = enc,
                // A code page this machine has no provider for cannot be chosen, and saying so plainly is
                // better than an item that silently falls back to guessing.
                Enabled = enc is not null,
            });
        }
        Item("UTF-&8", 65001);
        Item("UTF-16 &LE", 1200);
        Item("UTF-16 &BE", 1201);
        Item("&UTF-32 LE", 12000);
        Item("UTF-32 B&E", 12001);
        Item("&Windows-1252", 1252);
        Item($"&System default ({SystemCodePage})", SystemCodePage);
        // Worked out as the menu opens, rather than kept in step from every place a file can be opened.
        _miEncoding.DropDownOpening += (_, _) => SyncEncodingMenu();
        SyncEncodingMenu();
        return _miEncoding;
    }

    /// <summary>The code page Windows uses for programs that are not Unicode. <see cref="Encoding.Default"/>
    /// cannot answer this - it is always UTF-8 on .NET Core, so the item used to be a second copy of the
    /// UTF-8 one.</summary>
    private static int SystemCodePage => CultureInfo.CurrentCulture.TextInfo.ANSICodePage;

    private static Encoding? CodePage(int codePage)
    {
        try { return Encoding.GetEncoding(codePage); }
        catch (NotSupportedException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Ticks whichever entry is in effect, and says beside "Auto-detect" what it detected. That
    /// goes in the right-aligned hint rather than in the item's own text, so the item is still called the
    /// same thing whatever file is open.</summary>
    private void SyncEncodingMenu()
    {
        bool auto = true;
        foreach (ToolStripMenuItem item in _miEncoding.DropDownItems)
        {
            var enc = item.Tag as Encoding;
            item.Checked = auto ? _forcedEncoding is null : _forcedEncoding?.CodePage == enc?.CodePage;
            if (auto)
                item.ShortcutKeyDisplayString = string.IsNullOrEmpty(_doc.FilePath) || _forcedEncoding is not null
                    ? "" : EncodingDetector.DisplayName(_doc.Encoding);
            auto = false;
        }
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
        _busyLabel.AutoSize = false;
        _busyLabel.TextAlign = ContentAlignment.MiddleLeft;
        _busyLabel.Margin = new Padding(Dpi(6), 0, Dpi(4), 0);
        _busyLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _progressSlot = _progress.Width + _progress.Margin.Horizontal;
        // Sized for the wording alone; a search's counts widen it further, see EnsureActivitySlot.
        _baseActivitySlot = TextRenderer.MeasureText(WidestActivityText, _busyLabel.Font).Width + _progressSlot + Dpi(16);
        _activitySlot = _baseActivitySlot;
        _busyLabel.Width = _activitySlot;

        // Each metric is a fixed box, so a value growing a digit never shifts its neighbours. The UI font is
        // kept: its digits are already all the same width, so a monospaced face would buy no extra stability
        // and would cost the paths ~140px of room.
        foreach (var l in new[] { _selLabel, _filLabel, _totalLabel, _showLabel, _zoomLabel })
        {
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(Dpi(6), 0, Dpi(2), 0);
        }
        // Beside the count it qualifies, and boxed like the rest: a figure that appeared and disappeared
        // with the selection would shove everything to its right along twice a click.
        _elapsedLabel.AutoSize = false;
        _elapsedLabel.TextAlign = ContentAlignment.MiddleLeft;
        _elapsedLabel.Margin = new Padding(Dpi(6), 0, Dpi(2), 0);
        _elapsedLabel.Name = "stat.elapsed";
        // Section dividers. Every box on this side of the bar gets one, so the row reads as the list of
        // separate measurements it is - the counts used to run together as one long stretch of text, with
        // the widest of them sized for a file of a hundred million lines and so acres of space between the
        // number and the next word. The elapsed box takes one when it is there and its neighbour's divider
        // stands in for it when it is not, so nothing moves as the slot comes and goes.
        foreach (var l in new[] { _selLabel, _elapsedLabel, _filLabel, _totalLabel, _showLabel, _zoomLabel })
            l.BorderSides = ToolStripStatusLabelBorderSides.Left;

        _selLabel.Name = "stat.sel";
        _filLabel.Name = "stat.fil";
        _totalLabel.Name = "stat.total";
        _showLabel.Name = "stat.show";
        _zoomLabel.Name = "stat.zoom";
        _status.Items.AddRange(new ToolStripItem[]
        {
            // The label comes before the bar so the section's divider sits on an item whose left edge never
            // moves; with the bar first, hiding it dragged the divider left by the bar's width.
            _srcLabel, _filterLabel, _busyLabel, _progress,
            _selLabel, _elapsedLabel, _filLabel, _totalLabel, _showLabel, _zoomLabel
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
        long magnitude = MetricMagnitude();
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

    private long MetricMagnitude()
    {
        long actual = Math.Max(1_000, _doc.CompletedLineCount);
        long rounded = 9;
        while (rounded < actual) rounded = rounded * 10 + 9;

        long magnitude = Math.Max(99_999_999, rounded);
        // Only a window too narrow to afford that falls back to what this file actually needs.
        if (_status.Width > 0 && PathRoom(magnitude) < Dpi(300))
            magnitude = rounded;
        return magnitude;
    }

    /// <summary>What would be left for the two paths - everything on the bar that is not a fixed box. They
    /// are the only things on it that spring, so anything else given room is taking it from them.</summary>
    private int PathRoom(long magnitude)
        => _status.Width - TotalMetricWidth(magnitude) - _activitySlot - ElapsedSlotWidth();

    private int CurrentMetricWidth()
    {
        int total = 0;
        foreach (var l in MetricLabels) total += l.Width + l.Margin.Horizontal;
        return total;
    }

    private ToolStripStatusLabel[] MetricLabels => new[] { _selLabel, _filLabel, _totalLabel, _showLabel, _zoomLabel };

    private static string[] MetricSamples(long magnitude)
    {
        string n = magnitude.ToString("N0");
        return new[] { $"Sel: {n}", $"Fil: {n}", $"Total: {n}", ShowingAllLines, "Zoom: 400%" };
    }

    // The wider of the two, so the box does not change size when the mode does.
    private const string ShowingAllLines = "Showing: all lines";
    private const string ShowingMatchesOnly = "Showing: matches";

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

    /// <summary>Gives the elapsed slot its space only for a log whose clock could be read, so a log without
    /// one never loses the room to the two paths. Returns true if it moved.</summary>
    private bool EnsureElapsedSlot()
    {
        int want = ElapsedSlotWidth();
        bool has = want > 0;
        if (_elapsedLabel.Visible == has) return false;
        if (has) _elapsedLabel.Width = want;
        _elapsedLabel.Visible = has;
        return true;
    }

    /// <summary>How wide the slot has to be, or nothing when there is to be no slot. Sized from the widest
    /// value the wording can produce - not from the value on show, which changes with every click - and
    /// measured again only when the font moves under it, since measuring on the 33ms tick is not free.
    /// </summary>
    private int ElapsedSlotWidth()
    {
        if (!_settings.ShowElapsedInStatusBar || _doc.Clock is null) return 0;
        if (!ReferenceEquals(_elapsedSlotFont, _elapsedLabel.Font))
        {
            _elapsedSlotFont = _elapsedLabel.Font;
            _elapsedSlot = 0;
            foreach (string sample in ElapsedText.WidestStatus)
            foreach (string prefix in Prefixes)
                _elapsedSlot = Math.Max(_elapsedSlot,
                    TextRenderer.MeasureText(prefix + sample, _elapsedSlotFont).Width + Dpi(6));
        }
        return _elapsedSlot;
    }

    /// <summary>What the status bar calls each measurement. Named apart on purpose: one selected line gives
    /// the time since something, several give the stretch they cover, and those are different questions with
    /// answers orders of magnitude apart. One word over all of them would read as the number changing
    /// meaning under the reader.
    ///
    /// <para>The word after the delta is the whole point of the wording - a difference between two moments
    /// has a DIRECTION, and nothing in a signed number says which moment it was taken against.</para>
    /// </summary>
    private const string PrevPrefix = "\u0394 Prev: ", StartPrefix = "\u0394 Start: ",
                         RefPrefix = "\u0394 Ref: ", SpanPrefix = "Span: ";

    private static readonly string[] Prefixes = [PrevPrefix, StartPrefix, RefPrefix, SpanPrefix];

    private string OriginPrefix => EffectiveOrigin switch
    {
        ElapsedOrigin.FileStart => StartPrefix,
        ElapsedOrigin.Reference => RefPrefix,
        _ => PrevPrefix
    };

    /// <summary>How the origin is named in a sentence, so the tooltip reads as English rather than as the
    /// menu entry it came from.</summary>
    private string OriginSaid => EffectiveOrigin switch
    {
        ElapsedOrigin.FileStart => "the first line of the file",
        ElapsedOrigin.Reference => $"line {_doc.ReferenceLine + 1:N0}, the reference",
        _ => "the line above it on screen"
    };

    private void UpdateElapsed()
    {
        if (!_elapsedLabel.Visible) return;

        string prefix = OriginPrefix;
        if (!_grid.SelectionBounds(out long first, out long last))
        {
            _elapsedLabel.Text = prefix + ElapsedText.None;
            _elapsedLabel.ToolTipText = $"Select a line to see how long after {OriginSaid} it was written.";
            return;
        }

        if (first == last)
        {
            bool have = _doc.TryElapsedFrom(first, EffectiveOrigin, out long gap);
            _elapsedLabel.Text = prefix + (have ? ElapsedText.Status(gap) : ElapsedText.None);
            _elapsedLabel.ToolTipText = have
                ? gap == 0
                    // "Written 0 after line 21" is not a sentence, and on the reference itself it named the
                    // same line twice - which is the FIRST thing anybody reads, the reference having just
                    // been set from the line they are sitting on.
                    ? first == _doc.ReferenceLine && EffectiveOrigin == ElapsedOrigin.Reference
                        ? $"Line {first + 1:N0} is the reference everything is measured from."
                        : $"Line {first + 1:N0} carries the same time as {OriginSaid}."
                    : gap > 0
                        ? $"Line {first + 1:N0} was written {ElapsedText.Status(gap)} after {OriginSaid}."
                        // A log written by several threads at once really does arrive out of order, and a
                        // line above the reference is legitimately earlier than it - so a minus sign is
                        // information rather than a fault, and it needs saying or it reads as one.
                        : $"Line {first + 1:N0} was written {ElapsedText.Status(-gap)} BEFORE {OriginSaid}."
                : $"There is no time on this line, or none on {OriginSaid}, to measure from.";
            return;
        }

        bool measured = _doc.TrySpan(first, last, out long span);
        _elapsedLabel.Text = SpanPrefix + (measured ? ElapsedText.Status(span) : ElapsedText.None);
        _elapsedLabel.ToolTipText = measured
            ? span == 0
                ? $"Lines {first + 1:N0} to {last + 1:N0} all carry the same time."
                : $"Lines {first + 1:N0} to {last + 1:N0} cover {ElapsedText.Status(span)}."
            : "There is no time on the first or the last of the selected lines to measure between.";
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

    private static readonly SearchValues<char> PathSeparators = SearchValues.Create(['\\', '/']);

    /// <summary>Trims a path to fit, keeping the file name and as much of the head as there is room for.</summary>
    private static string Shorten(string text, int maxWidth, Font font)
    {
        if (maxWidth <= 0) return "";
        if (TextRenderer.MeasureText(text, font).Width <= maxWidth) return text;

        int cut = text.AsSpan().LastIndexOfAny(PathSeparators);
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
            else if (!a.StartsWith('/') && !a.StartsWith("--", StringComparison.Ordinal)) file = a;
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

    // ---- dropping files on the window ----

    /// <summary>Lets <paramref name="target"/> accept files dragged in from Explorer. A drop target is
    /// registered per window, and a child that has not asked for drops simply refuses them rather than
    /// passing them up - so every area a file might plausibly be aimed at has to opt in by name.</summary>
    private void AcceptFileDrops(Control target)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) => e.Effect = EffectForDrop(e);
        target.DragOver += (_, e) => e.Effect = EffectForDrop(e);
        target.DragDrop += (_, e) => { if (DroppedPaths(e) is { Length: > 0 } paths) OpenDroppedFiles(paths); };
    }

    private static DragDropEffects EffectForDrop(DragEventArgs e)
        => DroppedPaths(e) is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;

    /// <summary>The existing files being dragged, or null when the drag is not carrying any. Folders are
    /// left out: there is nothing sensible to open.</summary>
    internal static string[]? DroppedPaths(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return null;
        return e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? Array.FindAll(paths, File.Exists)
            : null;
    }

    internal static bool IsFilterFile(string path)
        => path.EndsWith(".cascade", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tat", StringComparison.OrdinalIgnoreCase);

    /// <summary>A dropped log replaces the one on screen, keeping the filters - which is the whole point of
    /// the gesture: one filter set, several files to try it against. Dropped filters are loaded first, so
    /// that dropping both together opens the file with them already applied.</summary>
    internal void OpenDroppedFiles(string[] paths)
    {
        if (Array.Find(paths, IsFilterFile) is { } filters) LoadFiltersFrom(filters);
        if (Array.Find(paths, p => !IsFilterFile(p)) is { } log) OpenFile(log, null);
    }

    private void OpenFile(string path, Encoding? enc)
    {
        // An export is reading the file that is about to be replaced. Opening waits for its readers to
        // stop, so leaving it running would freeze the window for exactly as long as this avoids.
        _saveCts?.Cancel();
        try
        {
            Cursor = Cursors.WaitCursor;
            bool sameFile = string.Equals(_doc.FilePath, path, StringComparison.OrdinalIgnoreCase);
            _doc.Open(path, enc);
            // The remembered crop belongs to the file it was set on, so another file clears the offer to
            // put it back. The document has already decided about the crop in force.
            if (!sameFile) { _lastCrop = null; ForgetBorrowedSelection(); }
            _forcedEncoding = enc;
            SyncEncodingMenu();
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

    /// <summary>Reads the file again from disk, keeping the encoding the reader chose - going back to
    /// guessing would undo their choice without saying so.</summary>
    private void Reload() { if (!string.IsNullOrEmpty(_doc.FilePath) && File.Exists(_doc.FilePath)) OpenFile(_doc.FilePath, _forcedEncoding); }

    private void ReopenWithEncoding(Encoding? enc) { if (!string.IsNullOrEmpty(_doc.FilePath) && File.Exists(_doc.FilePath)) OpenFile(_doc.FilePath, enc); }

    private void OpenFromClipboard()
    {
        if (!Clipboard.ContainsText()) return;
        string text = Clipboard.GetText();
        string tmp = Path.Combine(Path.GetTempPath(), "cascade_clip_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        OpenFile(tmp, null);
    }

    /// <summary>Writes the rows on show to a file. The writing itself is seconds of work on a large log -
    /// MEASURED at 1.7 s for 12 million lines - and it used to run on the thread that draws, so the window
    /// sat frozen for all of it. It now runs behind the window, which keeps drawing and scrolling, and Esc
    /// stops it. Cancelling or failing leaves the chosen file exactly as it was rather than truncated.</summary>
    private async void SaveCurrentLines()
    {
        if (_doc.RowCount == 0 || _saveCts is not null) return;
        using var dlg = new SaveFileDialog { Filter = "Text (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*", FileName = "filtered.txt" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        long rows = _doc.RowCount;
        using var cts = new CancellationTokenSource();
        _saveCts = cts;
        _saveFraction = 0;
        _saveWhat = $"Saving {rows:N0} lines to {Path.GetFileName(dlg.FileName)}";
        // Only recorded here; the refresh timer paints it, the same way indexing and filtering are shown.
        // Repainting the status bar on every report would put thousands of layouts on the UI thread.
        var progress = new Progress<double>(f => _saveFraction = f);
        UpdateStatus();
        try
        {
            await _doc.SaveRowsAsync(dlg.FileName, progress, cts.Token);
            ShowFindMessage($"Saved {rows:N0} lines", $"Saved {rows:N0} lines to {dlg.FileName}");
        }
        catch (OperationCanceledException)
        {
            ShowFindMessage("Save cancelled", $"{dlg.FileName} was left as it was");
        }
        catch (Exception ex)
        {
            // A path the dialog accepted can still fail every way a file can: a full disk, a drive pulled
            // out, a share that went away, a name the file system refuses. This runs from an async void, so
            // anything let through here would take the process down rather than report a failed save.
            ShowFindMessage("Could not save", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_saveCts, cts)) _saveCts = null;
            UpdateStatus();
        }
    }

    /// <summary>An export in progress. Closing the window or opening another file both stop it: it is
    /// holding the mapped file open, and waiting for it would be the freeze this avoided.
    /// <para>Kept apart from the find's hold on the activity slot rather than sharing it - a search started
    /// while an export runs would otherwise finish, clear the slot, and leave the export with no progress
    /// shown and nothing listening for Esc.</para></summary>
    private CancellationTokenSource? _saveCts;
    private double _saveFraction;
    private string _saveWhat = "";

    // ---- filters ----

    private void OnFiltersChanged()
    {
        // An edit that changed only how filters LOOK needs no pass over the file: every visible line is
        // already the right one, and both the log view and the map resolve a line's colour from the live
        // filter each time they paint. Deciding it from the two trees rather than trusting the caller to say
        // so means a path that forgets cannot get it wrong - it just re-filters, as it always did.
        bool appearanceOnly = _history.PendingRoots is { } before
                              && FilterCollection.SameMatching(before, _doc.Filters.Roots);
        // Every structural edit funnels through here, so this is where a snapshot taken before one becomes
        // an undo entry - or is dropped, when the tree turns out not to have changed.
        _history.Commit(_doc.Filters);
        SyncUndoMenu();
        // Which presets are in effect follows from which filters are enabled, so it is re-derived here
        // rather than tracked - ticking a filter by hand lights the matching preset up.
        _presets.RefreshActive();
        _filtersDirty = true;
        if (appearanceOnly)
        {
            // The list is already repainted by the SyncToModel every caller does before getting here.
            _grid.RefreshColors();
            UpdateTitle();
            UpdateStatus();
            return;
        }
        // Capture where the viewport is BEFORE the visible-line set changes, so the same line can be held at
        // the same place on screen while the new matches stream in.
        var anchor = _grid.CaptureViewAnchor();
        _doc.ApplyFilters();
        _grid.SetViewAnchor(anchor);
        _grid.RefreshView();
        _anchorActive = anchor.IsValid;
        UpdateTitle();
        UpdateStatus();
    }

    private void UndoFilterEdit() => ApplyHistory(_history.Undo(_doc.Filters), "Nothing to undo");

    private void RedoFilterEdit() => ApplyHistory(_history.Redo(_doc.Filters), "Nothing to redo");

    /// <summary>Puts a restored tree on screen. Deliberately not routed through <see cref="OnFiltersChanged"/>
    /// for the history's sake - the snapshot has already been swapped onto the other stack, and committing
    /// again here would record undoing as an edit of its own.
    ///
    /// It also cannot take that method's appearance-only shortcut, even when all the undo put back was a
    /// colour: a restore swaps in CLONES of the filters, so the snapshot still in force points at the
    /// instances that were replaced, and painting from it would show the styles that were just undone.</summary>
    private void ApplyHistory(string? label, string emptyMessage)
    {
        if (label is null) { ShowFindMessage(emptyMessage); return; }
        _history.Abandon();
        // In place, not a rebuild: the snapshot keeps every filter's id, so the list already has the rows
        // and only what the undo actually changed has to be redrawn.
        _filterTree.SyncToModel();
        _filtersDirty = true;
        var anchor = _grid.CaptureViewAnchor();
        _doc.ApplyFilters();
        _grid.SetViewAnchor(anchor);
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

    /// <summary>Makes a filter and puts it where <paramref name="placement"/> asks. The place is only where
    /// the choice starts: the dialog offers all three and answers with whichever the reader settled on, so a
    /// key pressed in the log can be changed for another one after the dialog is already open.
    ///
    /// <para>"Above" and "as a child" are measured from the filter the list is on. There need not be one -
    /// nothing is selected in a freshly opened file - and nesting can run out of levels, so the dialog shuts
    /// those choices off rather than refusing the command; asking for a place that is not there simply lands
    /// in the default one.</para></summary>
    private void AddFilter(NewFilterPlacement placement, string pattern = "", string caution = "")
    {
        var anchor = _filterTree.SelectedFilter;
        var filter = new Filter { Enabled = true, Match = { Text = pattern } };
        using var dlg = new FilterEditDialog(filter, isNew: true, _doc.Filters.EnumerateDepthFirst().ToList(),
                                             parent: null, ViewDefaults) { Caution = caution };
        dlg.OfferPlacements(placement, anchor, _settings.AddNewFiltersAtTop);
        _history.Begin("Add Filter", _doc.Filters);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var (parent, index) = NewFilterSpot(dlg.Placement, anchor, _settings.AddNewFiltersAtTop, _doc.Filters);
            _doc.Filters.Add(filter, parent, index);
            _filterTree.SyncToModel();
            _filterTree.RevealFilter(filter);
            OnFiltersChanged();
        }
        else _history.Abandon();
    }

    private void EditFilter(Filter filter)
    {
        if (_filterTree.SelectedCount > 1) { EditAppearance(_filterTree.SelectedFilters); return; }
        using var dlg = new FilterEditDialog(filter, isNew: false, _doc.Filters.EnumerateDepthFirst().ToList(),
                                             filter.Parent, ViewDefaults);
        _history.Begin("Edit Filter", _doc.Filters);
        if (dlg.ShowDialog(this) == DialogResult.OK) CommitFilterEdit();
        else _history.Abandon();
    }

    /// <summary>What an accepted edit of one filter does. The rows are already there, so only what changed is
    /// touched, and the funnel works out for itself whether anything needs re-filtering.</summary>
    private void CommitFilterEdit()
    {
        _filterTree.SyncToModel();
        OnFiltersChanged();
    }

    /// <summary>Test seam: an edit accepted in the filter dialog, which is modal and so cannot be driven from
    /// a headless check. <paramref name="edit"/> stands in for what the dialog writes to the filter.</summary>
    internal void EditFilterForTesting(Action edit)
    {
        _history.Begin("Edit Filter", _doc.Filters);
        edit();
        CommitFilterEdit();
    }

    /// <summary>Appearance, and only appearance, for a group of filters: a pattern belongs to one filter, so
    /// there is nothing for the rest of the editor to show.</summary>
    private void EditAppearance(IReadOnlyList<Filter> filters)
    {
        if (filters.Count == 0) return;
        using var dlg = new AppearanceDialog(filters, _doc.Filters.EnumerateDepthFirst().ToList(), ViewDefaults);
        _history.Begin(filters.Count == 1 ? "Edit Filter" : $"Edit {filters.Count} Filters", _doc.Filters);
        bool changed = false;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            foreach (var f in filters) changed |= dlg.Change.ApplyTo(f);

        if (!changed) { _history.Abandon(); return; }
        _filterTree.SyncToModel();
        OnFiltersChanged();
    }

    /// <summary>What the log view draws with when no filter says otherwise - the bottom of the inheritance
    /// chain, so the filter dialog can show what an unset colour will actually look like.</summary>
    private ResolvedStyle ViewDefaults =>
        new(new RgbColor(_settings.Foreground.R, _settings.Foreground.G, _settings.Foreground.B),
            new RgbColor(_settings.Background.R, _settings.Background.G, _settings.Background.B), false, false);

    /// <summary>What a filter made from a log line starts out matching: the line itself, less the space
    /// around it. Only the first 200 characters used to be kept, which threw away most of exactly the long
    /// lines that are worth building a filter from.</summary>
    internal static string SeedPatternFromLine(string line)
    {
        string text = line.Trim();
        return text.Length <= FilterEditDialog.MaxPatternLength ? text : text[..FilterEditDialog.MaxPatternLength];
    }

    private void CreateFilterFromLine(long line)
        => CreateFilterFromShownText(SeedPatternFromLine(DisplayedLine(line)), line);

    /// <summary>Ctrl+N and its two neighbours: a filter from whatever is selected, going where the key that
    /// was pressed asks for. Part of a line if part of one is selected - which is the point of being able to
    /// select part of one - and otherwise the whole caret line.</summary>
    private void NewFilter(NewFilterPlacement placement)
    {
        long line = _grid.CaretLine;
        string? seed = NewFilterSeed(_grid.SelectedText, line >= 0 ? DisplayedLine(line) : null);
        if (seed is null) AddFilter(placement);
        else AddFilter(placement, seed, CautionAboutShownText(seed, line));
    }

    /// <summary>The line as it is being SHOWN, which is what a filter seeded from it should start out
    /// matching - what is on screen is what the reader means.</summary>
    private string DisplayedLine(long line) => _grid.DisplayedLineText(line);

    /// <summary>Filters and searches run on the whole raw line, so text that only exists because fields have
    /// been hidden or carried about matches nothing. The question is not "has anything been hidden" but the
    /// exact one: does this seed actually occur in the line it came from? Hiding a field at the FRONT still
    /// leaves the rest of the line in one piece, and warning about that would be crying wolf.</summary>
    private string CautionAboutShownText(string seed, long line)
        => line >= 0 && ShownTextNeedsCaution(_doc.Columns.Active, seed, _doc.GetLineText(line))
           ? RawLineCaution : "";

    /// <summary>The whole rule, in one line: warn when what was taken off the screen is not IN the line it
    /// was taken from, and never otherwise.
    ///
    /// <para>This is why the warning comes and goes. With the fields off, or laid out in COLUMNS - where a
    /// selection is indices into the raw line and the cells are only a way of drawing it - the seed is
    /// always the file's own text, so nothing is ever said. Laid out INLINE it is said only when the seed
    /// crosses somewhere the row was changed: a gap left by a field hidden in the middle, a join made by
    /// carrying a field elsewhere, or a space the projection had to invent. A seed lying wholly inside one
    /// surviving stretch is the file's own text and will match, and saying otherwise would teach the reader
    /// to ignore the warning for the times it matters.</para></summary>
    internal static bool ShownTextNeedsCaution(bool fieldsOn, string seed, string rawLine)
        => fieldsOn && seed is { Length: > 0 } && !rawLine.Contains(seed, StringComparison.Ordinal);

    /// <summary>Said wherever text taken off the screen is about to be matched against the file. Fields
    /// change what a line LOOKS like and change nothing about what it IS, and that is the whole of it.</summary>
    internal const string RawLineCaution =
        "Searching and filtering always run on the original line, not the one shown.";

    /// <summary>Starts a filter from a seed taken off the screen, warning when the screen and the file no
    /// longer agree. Every path that seeds from a line comes through here. A filter made this way goes to the
    /// default place: a double-click in the log says nothing about the filter list.</summary>
    private void CreateFilterFromShownText(string seed, long line)
        => AddFilter(NewFilterPlacement.Default, seed, CautionAboutShownText(seed, line));

    /// <summary>What a filter started with Ctrl+N arrives matching: the part of a line that is picked out,
    /// else the whole caret line, else nothing at all. Nothing is on the caret until something has been
    /// clicked, which is how a file opens - and an empty filter is more use there than being told to go and
    /// select a line first.</summary>
    internal static string? NewFilterSeed(string? selected, string? caretLine)
        => selected is { Length: > 0 } part ? SeedPatternFromLine(part)
         : caretLine is { } whole ? SeedPatternFromLine(whole)
         : null;

    /// <summary>Double-clicking a line in the log. The part of it that was picked out if there was one,
    /// otherwise the whole line - the click that began the double-click has already put the caret there.</summary>
    private void NewFilterFromDoubleClick(string? picked)
    {
        long line = _grid.CaretLine;
        if (picked is { Length: > 0 })
        {
            CreateFilterFromShownText(SeedPatternFromLine(picked), line);
            return;
        }
        if (line >= 0) CreateFilterFromLine(line);
    }

    /// <summary>Where a new filter lands: the list it belongs to and the place in it. An index of -1 means
    /// the end of that list.
    ///
    /// <para>Above puts it exactly where the anchor is now, which pushes the anchor down - a sibling list
    /// closes up behind an insert, so no arithmetic is needed to say "in front of this one". The other two
    /// place it among the roots or among the anchor's own children, at whichever end the preference names.
    /// Anything that cannot be done - no anchor to measure from, or a child that would nest past the deepest
    /// level - falls back to the default place, which is the one the dialog was offering anyway.</para></summary>
    internal static (Filter? Parent, int Index) NewFilterSpot(NewFilterPlacement placement, Filter? anchor,
                                                              bool addAtTop, FilterCollection filters)
    {
        int end = addAtTop ? 0 : -1;
        if (anchor is null) return (null, end);
        return placement switch
        {
            NewFilterPlacement.Above =>
                (anchor.Parent, Math.Max(0, (anchor.Parent?.Children ?? filters.Roots).IndexOf(anchor))),
            NewFilterPlacement.Child when anchor.Depth + 1 < FilterCollection.MaxDepth => (anchor, end),
            _ => (null, end)
        };
    }

    private void FindSelectedFilterMatch(bool forward)
    {
        if (_filterTree.SelectedFilter is { } f) FindFilterMatch(f, forward);
    }
    private async void FindFilterMatch(Filter filter, bool forward)
    {
        if (string.IsNullOrEmpty(_doc.FilePath)) return;
        long caret = _grid.CaretLine;
        long start = caret < 0 ? (forward ? _doc.FirstDisplayLine : _doc.LastDisplayLine) : caret + (forward ? 1 : -1);

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
        if (!busy && !_findBusy) return;   // nothing to take down, and this is called after every search
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

    /// <summary>Reads a filter set in, replacing whatever is on screen. THE one funnel for every way of
    /// doing that - the menu, the recent list, importing a .tat, a dropped file and the startup auto-load -
    /// which is why the offer to save what is being thrown away belongs here rather than at each caller.
    /// Re-opening the file already open needs no special case: it saves, then reads the same file back.</summary>
    private void LoadFiltersFrom(string path)
    {
        if (!OfferToSaveFilters()) return;
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
                if (cols is not null) _doc.Columns.CopyFrom(cols);
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
    /// from being auto-loaded next launch. Offers to save first, exactly as closing the window does - it is
    /// the same loss either way, and it happens on one menu click with nothing to undo it.</summary>
    private void CloseFilters()
    {
        if (!OfferToSaveFilters()) return;
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

    /// <summary>Writes the filters out, asking where to put them when there is nowhere yet. Returns whether
    /// they actually reached disk: saying "yes, save" and then dismissing the file dialog must not let the
    /// thing that asked go ahead and discard them anyway.</summary>
    private bool SaveFilters(bool saveAs)
    {
        string? path = _filterFilePath;
        if (saveAs || path is null || !path.EndsWith(".cascade", StringComparison.OrdinalIgnoreCase))
        {
            using var dlg = new SaveFileDialog { Filter = "Cascade filters (*.cascade)|*.cascade", FileName = Path.GetFileNameWithoutExtension(path) ?? "filters" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
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
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        return false;
    }

    // ---- view ----

    private void ToggleFilteredMode()
    {
        // Capture the anchor in the CURRENT mode before flipping, so it maps the current row correctly.
        var anchor = _grid.CaptureViewAnchor();
        _doc.Filters.ShowOnlyFilteredLines = !_doc.Filters.ShowOnlyFilteredLines;
        _filtersDirty = true;
        UpdateTitle();
        SyncFilteredModeMenu();
        // Filtered vs. dim is a display-only mode: the matched set is unchanged, so there is no need to
        // re-run filtering (which would blank the view). Just re-map the view, holding the line where it
        // already is - the same as every filter change does.
        _grid.SetViewAnchor(anchor);
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
        _filterTree.SetPresetsEdge(!_settings.ShowFilterPresets ? DockStyle.None
                                   : _filterPane.Orientation == Orientation.Vertical ? DockStyle.Right
                                   : DockStyle.Bottom);
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

    /// <summary>Puts the divider where the log view holds a whole number of lines, so there is never a strip
    /// of dead space under the last one. Only when the filter list is above or below - down one side the
    /// divider sets a width, which has nothing to do with line height.
    ///
    /// Snaps to the NEAREST such position, and takes the smaller number of lines only when the larger one
    /// will not fit. Nearest rather than rounding up because the chrome this measures from can change under
    /// it - hiding the sideways scrollbar to wrap moves the lattice - and always rounding up then hands the
    /// log another line on every change and never gives one back.</summary>
    private void SnapSplitter()
    {
        if (_snapping || _split.Orientation != Orientation.Horizontal) return;
        if (_split.Panel1Collapsed || _split.Panel2Collapsed) return;
        // ChromeHeight already counts the find bar: it is hosted inside the log view, so from the divider's
        // point of view it is more chrome standing between the split and the first line of text.
        int pitch = _grid.RowPitch, chrome = _grid.ChromeHeight;
        int total = _split.Height - _split.SplitterWidth;
        if (pitch <= 1 || total <= 0) return;

        bool gridFirst = _treePanel != 1;
        int gridHeight = gridFirst ? _split.SplitterDistance : total - _split.SplitterDistance;
        int lines = Math.Max(1, (int)Math.Round((gridHeight - chrome) / (double)pitch, MidpointRounding.AwayFromZero));
        int low = _split.Panel1MinSize, high = Math.Max(low, total - _split.Panel2MinSize);

        int Distance(int n) => gridFirst ? chrome + n * pitch : total - (chrome + n * pitch);
        int wanted = Distance(lines);
        if (wanted < low || wanted > high) wanted = Distance(Math.Max(1, lines - 1));
        wanted = Math.Clamp(wanted, low, high);
        if (wanted == _split.SplitterDistance) return;

        _snapping = true;
        try { _split.SplitterDistance = wanted; }
        catch { /* sizes not ready */ }
        finally { _snapping = false; }
    }

    /// <summary>The room the divider has to move in, measured the way it is currently turned.</summary>
    private int SplitTravel =>
        (_split.Orientation == Orientation.Vertical ? _split.Width : _split.Height) - _split.SplitterWidth;

    /// <summary>The filter list's share of the window - whichever of the two saved fractions applies to the
    /// way the panes are turned right now.</summary>
    private double FilterListFraction
    {
        get => SaneShare(_split.Orientation == Orientation.Vertical
                             ? _settings.FilterListWidthFraction
                             : _settings.FilterListHeightFraction);
        set
        {
            if (_split.Orientation == Orientation.Vertical) _settings.FilterListWidthFraction = value;
            else _settings.FilterListHeightFraction = value;
        }
    }

    /// <summary>A share that leaves both panes on screen. Also the guard against a hand-edited settings file
    /// - or one from a version that never wrote this - handing over a nonsense number.</summary>
    private static double SaneShare(double fraction) => double.IsFinite(fraction) ? Math.Clamp(fraction, 0.05, 0.95) : 0.3;

    /// <summary>Puts the divider where the saved share asks for.
    ///
    /// <para>What is stored is the filter LIST's share, not the divider's own position: the list moves
    /// between the two panels as it is docked to one edge or another, so a number measured from the divider
    /// would mean the log on some edges and the list on others.</para></summary>
    private void ApplyFilterListSize()
    {
        int total = SplitTravel;
        if (total <= 0) return;
        // The 60px floor is what docking has always given a list on a small window - a pane too narrow to
        // read a filter in is no more use than no pane at all.
        int list = Math.Clamp((int)Math.Round(total * FilterListFraction), Math.Min(60, total / 2), total - 1);
        WhileArranging(() =>
        {
            try { _split.SplitterDistance = _treePanel == 1 ? list : total - list; }
            catch { /* sizes not ready */ }
        });
    }

    /// <summary>Runs a layout change without recording the divider positions it passes through. Rearranging
    /// the panes moves the divider several times on the way to where it is being put - re-parenting a
    /// control alone lays the split out again - and none of those are the user's doing. Recorded, they would
    /// overwrite the very share being restored with whatever the window looked like halfway through it.
    /// Nested, because sizing the panes is part of docking them.</summary>
    private void WhileArranging(Action change)
    {
        bool was = _arranging;
        _arranging = true;
        try { change(); }
        finally { _arranging = was; }
    }

    /// <summary>Records the share the divider has just been left at. Nothing is written while the app is
    /// moving the divider itself or while the window is still being laid out, and nothing is written for a
    /// move too small to have been meant - a resize re-rounds the divider to whole lines, and a settings
    /// file rewritten on every drag of a window edge is a settings file being written for no reason.
    /// </summary>
    private void RememberFilterListSize()
    {
        if (_arranging || !_layoutSettled || _split.Panel1Collapsed || _split.Panel2Collapsed) return;
        int total = SplitTravel;
        if (total <= 0) return;
        double fraction = SaneShare((_treePanel == 1 ? _split.SplitterDistance : total - _split.SplitterDistance) / (double)total);
        if (Math.Abs(fraction - FilterListFraction) < 0.005) return;
        FilterListFraction = fraction;
        SaveSettingsSoon();
    }

    /// <summary>Moves the filter list to an edge and remembers it. The list arrives at whatever share of the
    /// window it was last given on that edge, and a hidden list is brought back, so all three parts of where
    /// the list is stay in step.</summary>
    private void SetFilterDock(FilterDock dock)
    {
        ApplyFilterDock(dock);
        SetFilterListVisible(true);
        _settings.FilterListDock = dock;
        SaveSettingsSoon();
    }

    /// <summary>The layout half of docking, with nothing recorded - so restoring the saved edge at startup
    /// and applying an imported one go through the very code the menu item does.
    ///
    /// <para>An edge already in effect is left completely alone. Re-docking rebuilds the panes and gives the
    /// list back the share saved for that edge, which is what moving it should do and emphatically not what
    /// pressing OK in Preferences should: that would throw away a divider the user had just dragged.</para>
    /// </summary>
    private void ApplyFilterDock(FilterDock dock)
    {
        bool treeFirst = dock is FilterDock.Top or FilterDock.Left;
        var orientation = dock is FilterDock.Left or FilterDock.Right ? Orientation.Vertical : Orientation.Horizontal;
        int wantedPanel = treeFirst ? 1 : 2;
        // The panel the tree sits in and the way the divider is turned name one of the four edges between
        // them, so together they say whether there is anything to do.
        if (_treePanel == wantedPanel && _split.Orientation == orientation) return;

        WhileArranging(() => WithoutRedraw(() =>
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
            ApplyFilterListSize();
            _split.ResumeLayout();
        }));
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

    /// <summary>Whether the filter list is on screen, read off the pane rather than the setting: the two are
    /// kept in step through <see cref="SetFilterListVisible"/>, and the pane is the one that is true.</summary>
    private bool FilterListVisible => !(_treePanel == 1 ? _split.Panel1Collapsed : _split.Panel2Collapsed);

    private void ToggleFilterList() => SetFilterListVisible(!FilterListVisible);

    private void EnsureFilterListVisible() => SetFilterListVisible(true);

    /// <summary>Shows or hides the filter list and remembers which. The pane is set either way - this is
    /// also how the saved state is put back at startup, when the setting already says what is wanted - and
    /// only a preference that actually moved is worth writing a settings file for.</summary>
    private void SetFilterListVisible(bool visible)
    {
        if (_treePanel == 1) _split.Panel1Collapsed = !visible;
        else _split.Panel2Collapsed = !visible;
        if (_settings.ShowFilterList == visible) return;
        _settings.ShowFilterList = visible;
        SaveSettingsSoon();
    }

    private void FocusTextArea() => _grid.Focus();

    private void FocusFilterList() { EnsureFilterListVisible(); _filterTree.FocusList(); }

    private void FocusFilterSearch() { EnsureFilterListVisible(); _filterTree.ShowSearch(); }

    /// <summary>Tab / Shift+Tab cycles focus between the two main areas: the log view and the filter list.
    /// The filter search bar is not one of them - it is a thing you open, use and dismiss, like the find
    /// bar, and tabbing into it when it is not even on screen would be a stop at nothing.</summary>
    private void CycleFocus(bool forward)
    {
        _ = forward;   // with two areas both directions are the same move
        if (_grid.Focused) FocusFilterList();
        else FocusTextArea();
    }

    private bool IsTextInputFocused() => FocusedTextInput() is not null;

    /// <summary>The box a keystroke is being typed into, or null when the focus is anywhere else.</summary>
    private Control? FocusedTextInput()
    {
        Control? c = ActiveControl;
        while (c is ContainerControl cont && cont.ActiveControl is not null) c = cont.ActiveControl;
        return c is TextBoxBase or ComboBox or NumericUpDown ? c : null;
    }

    /// <summary>
    /// The editing keys, given to the box being typed into rather than to the log or the filter list.
    ///
    /// <para>A menu shortcut is dispatched by the form BEFORE the control with the focus is ever offered the
    /// key, so the log's own Ctrl+A and Ctrl+C reached over the find bar and selected and copied the whole
    /// log while the caret was sitting in the term - and Ctrl+Z undid a filter edit in the middle of typing
    /// one. Every one of these means something else inside a text box, and the text box is what has to get
    /// them.</para>
    ///
    /// <para>Only the four the menu claims are here. Cut and paste are nobody else's, so they already reach
    /// the box on their own and are left alone.</para>
    /// </summary>
    private bool EditFocusedText(Keys keyData)
    {
        if (FocusedTextInput() is not { } box) return false;
        switch (keyData)
        {
            case Keys.Control | Keys.A:
                if (box is TextBoxBase all) all.SelectAll();
                else if (box is ComboBox combo) combo.SelectAll();
                else return false;
                return true;

            case Keys.Control | Keys.C:
                if (box is TextBoxBase source) source.Copy();
                else if (box is ComboBox picked) CopyText(picked.SelectedText);
                else return false;
                return true;

            // A Windows text box undoes its own typing, and has no redo at all - so Ctrl+Y is taken off the
            // filter list here and given to nobody, which is what every other text box on the system does.
            case Keys.Control | Keys.Z:
                if (box is TextBoxBase typed) typed.Undo();
                else Undo(box);
                return true;
            case Keys.Control | Keys.Y:
                return true;

            default:
                return false;
        }
    }

    private const int WM_UNDO = 0x0304;

    /// <summary>Undoes the typing in a box WinForms gives no Undo of its own - the find bar's combo. The
    /// message has to go to the edit window inside it: the combo does not pass this one on, so sent to the
    /// combo itself it is quietly dropped and Ctrl+Z does nothing at all.</summary>
    private static void Undo(Control box)
    {
        IntPtr edit = FindWindowEx(box.Handle, IntPtr.Zero, "EDIT", null);
        if (edit != IntPtr.Zero) SendMessage(edit, WM_UNDO, IntPtr.Zero, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    private static void CopyText(string text)
    {
        if (text.Length == 0) return;   // nothing selected: a text box copies nothing rather than clearing
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    private void ShowColumns()
    {
        string before = _doc.Columns.Describe();
        var samples = SampleLines(out int caretAt);
        using var dlg = new ColumnsDialog(_doc.Columns, samples, caretAt, _doc.Columns.HasTime ? null : _doc.Clock);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _doc.Columns.CopyFrom(dlg.Result);
            // Columns live in the filter file, so changing them is an unsaved change like any other.
            if (_doc.Columns.Describe() != before) { _filtersDirty = true; UpdateTitle(); }
            FieldsChanged();
            _grid.RefreshView();
        }
    }

    /// <summary>The lines the dialog reads: the screenful in front of the reader, with the caret's own line
    /// among them and <paramref name="caretAt"/> saying which it is. A whole window rather than one line,
    /// because a template that happens to fit the line under the caret and nothing else is exactly the trap
    /// the "lines that match" count exists to spring - but the caret's line is still the one the dialog
    /// opens on, and the one Detect reads.</summary>
    private List<string> SampleLines(out int caretAt)
    {
        caretAt = 0;
        var lines = new List<string>();
        if (_doc.CompletedLineCount <= 0) return [""];

        long caret = _grid.CaretLine < 0 ? 0 : _grid.CaretLine;
        long row = Math.Max(0, _doc.RowForLine(caret));
        for (long i = row; i < _doc.RowCount && lines.Count < 200; i++)
            lines.Add(_doc.GetLineText(_doc.RowToLine(i)));
        for (long i = row - 1; i >= 0 && lines.Count < 200; i--)
        {
            lines.Insert(0, _doc.GetLineText(_doc.RowToLine(i)));
            caretAt++;
        }

        return lines.Count == 0 ? [""] : lines;
    }

    /// <summary>Turns splitting on and off from the menu. Turning it on with nothing set up would otherwise
    /// show an empty header, so the parts are read off the current line first - and if the line does not
    /// offer any, the settings open instead of nothing happening.</summary>
    private void ToggleColumns()
    {
        if (_doc.Columns.Enabled)
        {
            SetColumnsEnabled(false);
            return;
        }

        if (_doc.Columns.Columns.Count == 0)
        {
            var samples = SampleLines(out int caretAt);
            string template = LineTemplate.Detect(samples[Math.Min(caretAt, samples.Count - 1)]);
            if (template.Length == 0) { ShowColumns(); return; }
            _doc.Columns.Template = template;
            _doc.Columns.Reset();
        }
        SetColumnsEnabled(true);
    }

    /// <summary>Puts the log into one layout or the other, turning splitting on if it was off. The two
    /// strips need not be the same height - the chips are labelled in the window's font, the header in the
    /// log's - so the switch keeps the line being read where it is, as turning fields on does.</summary>
    private void SetLayout(FieldLayout layout)
    {
        if (_doc.Columns.Enabled && _doc.Columns.Layout == layout) return;
        if (!_doc.Columns.Enabled) { _doc.Columns.Layout = layout; ToggleColumns(); return; }

        _grid.KeepTextStillAcrossFieldChange(() =>
        {
            _doc.Columns.Layout = layout;
            _grid.RefreshView();
        });
        FieldsChanged();
        _filtersDirty = true;
        UpdateTitle();
    }

    /// <summary>Re-ticks the menu to match the state. Called when the menu opens as well as after a change,
    /// so it must not DO anything - see the callers for what a change itself has to put right.
    ///
    /// <para>The key that switches layout is shown against the layout it would switch TO, which is the only
    /// place it can be read without a sentence explaining it: the ticked one is where you are, and the key
    /// beside the other one is how you get there. It is drawn, not registered - the form itself answers the
    /// key, so nothing has to be shuffled between two items every time the layout changes.</para></summary>
    private void SyncColumnsMenu()
    {
        bool on = _doc.Columns.Enabled;
        _miColumns.Checked = on;
        bool grid = on && _doc.Columns.Layout == FieldLayout.Columns;
        _miLayoutColumns.Checked = grid;
        _miLayoutInline.Checked = on && _doc.Columns.Layout == FieldLayout.Inline;
        _miLayoutColumns.ShortcutKeyDisplayString = on && !grid ? SwitchLayoutKeyName : null;
        _miLayoutInline.ShortcutKeyDisplayString = grid ? SwitchLayoutKeyName : null;
    }

    private const Keys SwitchLayoutKey = Keys.Control | Keys.Shift | Keys.X;
    private const string SwitchLayoutKeyName = "Ctrl+Shift+X";

    /// <summary>Flips between the two layouts. Only while fields are being split: with splitting off there
    /// is no layout to be in, and turning it on from a key that says "switch" would be a surprise.</summary>
    private bool SwitchLayout()
    {
        if (!_doc.Columns.Enabled) return false;
        SetLayout(_doc.Columns.Layout == FieldLayout.Columns ? FieldLayout.Inline : FieldLayout.Columns);
        return true;
    }

    /// <summary>What is on screen has changed, so a note about a match being out of sight no longer answers
    /// for anything: it is worked out afresh the next time a search lands somewhere.
    /// <para>The status bar is re-read here too. Naming the field that holds the timestamp brings a clock
    /// into being, and nothing else about the window changes - so the 33ms tick, which only looks again
    /// when the counts or the busy state move, would leave the bar as it was until something else did.
    /// </para></summary>
    private void FieldsChanged()
    {
        _hiddenMatch = "";
        SyncColumnsMenu();
        SyncElapsedMenu();
        UpdateStatus();
    }

    /// <summary>Turns the header strip on or off, keeping the line the reader is looking at exactly where
    /// it is. The strip takes rows off the top of the text - the header in one layout, the chips in the
    /// other - so without this the whole log appears to slide.</summary>
    private void SetColumnsEnabled(bool on)
    {
        _grid.KeepTextStillAcrossFieldChange(() =>
        {
            _doc.Columns.Enabled = on;
            _grid.RefreshView();
        });
        FieldsChanged();
        _filtersDirty = true;
        UpdateTitle();
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
        _miWordWrap.Checked = _settings.WordWrap;
        _miFilterTips.Checked = _settings.ShowFilterTooltips;
        _grid.SetMatchMapVisible(_settings.ShowMatchMap);
        LayoutPresetPane();
        // The dock leaves an edge already in effect alone, so this costs nothing on the Preferences path and
        // rearranges the window on the import one - where the share may have arrived changed as well.
        ApplyFilterDock(_settings.FilterListDock);
        ApplyFilterListSize();
        SetFilterListVisible(_settings.ShowFilterList);
        SyncMarkersMenu();
        _grid.ApplySettings(_settings);
        // The line height may have moved with it, and the bar is measured in whole log lines.
        _findBar.SnapHeightTo(_grid.RowPitch);
        SnapSplitter();
        _filterTree.SetSettings(_settings);
        RefreshRecentMenus();
        SyncHangWatchdog();
        UpdateStatus();
    }

    /// <summary>Starts or stops the hang watchdog to match the preference. Built fresh rather than adjusted,
    /// because the only things it holds are the window and how long to wait.</summary>
    private void SyncHangWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = HangWatchdog.Start(this, _settings);
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

    private void BuildFindBar()
    {
        _findBar = new FindBar(DoFind);
        _findBar.History = () => _state.RecentFindTerms;
        _findBar.CloseRequested += CloseFind;
        // Typing marks the hits already on screen and nothing else: no sweep, and the view does not
        // move until a search is actually asked for.
        _findBar.PreviewChanged += q =>
        {
            _grid.SetFindHighlight(q is null ? null : FindEngine.CompileQuery(q));
            if (q is null && _lastQuery is not null) ClearFind();
        };
    }

    /// <summary>Brings the bar up. <paramref name="focus"/> is false for F3, which repeats a search without
    /// taking the keyboard away from the log - the arrow keys have to keep working between matches.</summary>
    private void ShowFind(bool focus = true)
    {
        if (!_findBar.Visible)
        {
            _findBar.SetHistory(_state.RecentFindTerms);
            // A whole number of lines, so the divider below never has to move to make the rest fit.
            _findBar.SnapHeightTo(_grid.RowPitch);
            int taken = _findBar.Height / _grid.RowPitch;
            // Painting off while the log resizes: it would otherwise draw a row where the horizontal
            // scrollbar is about to be, and the scrollbar would then cover it - a flash along the bottom.
            WithoutRedraw(() => _grid.KeepTextStillAcross(taken, () =>
            {
                _findBar.Visible = true;
                SnapSplitter();
            }));
            CautionInFindBar();
        }
        // A part of a line picked out in the log is almost always what the search is about to be for.
        // Whole lines are not: selecting them is how you copy or mark them, and a line's worth of text is
        // no kind of search term - so only a selection WITHIN a line seeds the box.
        if (focus && _grid.SelectedText is { Length: > 0 } picked) _findBar.SetTerm(picked);
        if (focus) _findBar.FocusInput();
    }

    /// <summary>Said in the bar as it opens, while the log is being shown in fields: what is on screen is
    /// not what will be searched, and the moment to say so is before anything has been typed. It gives way
    /// to the tally the first search puts there and does not come back until the bar has been closed and
    /// opened again - a warning that reappears between searches is one that gets read once and then
    /// stopped being read.</summary>
    private void CautionInFindBar()
    {
        if (_doc.Columns.Active) _findBar.SetMessage(RawLineCaution, caution: true);
    }

    /// <summary>Puts the bar away and the term with it. One gesture, because a search that is running with
    /// nothing on screen to say so is the state this bar exists to remove.</summary>
    private void CloseFind()
    {
        if (!_findBar.Visible && _lastQuery is null) return;
        ClearFind();
        int given = _findBar.Visible ? _findBar.Height / _grid.RowPitch : 0;
        WithoutRedraw(() => _grid.KeepTextStillAcross(-given, () =>
        {
            _findBar.Visible = false;
            SnapSplitter();
        }));
        _findBar.SetMessage("");
        FocusTextArea();
    }

    private async void DoFind(FindQuery query, bool forward)
    {
        bool sameTerm = _lastQuery == query;
        _lastQuery = query;
        // A different term counts differently, and nothing else here would notice if it happened to land on
        // the line the last one did.
        if (!sameTerm) _tally = _tallyDetail = "";
        if (_state.AddRecentFindTerm(query.Text)) _stateDirty = true;
        // The highlight outlives the dialog: F3 keeps working with it closed, so the hits have to stay
        // marked until the term is deliberately put away.
        if (!sameTerm) _grid.SetFindHighlight(FindEngine.CompileQuery(query));
        long start = _grid.CaretLine;
        start = start < 0 ? 0 : start + (forward ? 1 : -1);

        // Ask first, and only put up the progress UI if the answer is not already known. Everything the
        // sweep has covered answers at once, which is the whole point of it - and showing then hiding a
        // progress bar around an instant answer cost fifteen times the search itself, so holding Enter down
        // on a common term looked like the window had stopped responding.
        var pending = _doc.FindNextAsync(query, start, forward);
        if (!pending.IsCompleted)
        {
            // F3 usually repeats the search with the dialog closed, so the progress has to reach the status
            // bar too - a search that is waiting on the sweep would otherwise look like a hang.
            SetFindBusy(true, "Searching", $"Searching for {Quote(query.Text)}", () => _doc.FindProgressFor(forward));
            _findBar.SetSearching(true);
        }
        long found;
        try
        {
            found = await pending;
        }
        catch (OperationCanceledException)
        {
            // The user cancelled, or a newer search superseded this one. Only reset when nothing is still
            // running (i.e. a genuine cancel, not a supersede that already re-armed it).
            if (!_doc.IsFindRunning)
            {
                SetFindBusy(false);
                _findBar.SetSearching(false);
            }
            return;
        }
        catch (ObjectDisposedException)
        {
            // The term was put away while this was still waiting - emptying the box does that.
            SetFindBusy(false);
            _findBar.SetSearching(false);
            return;
        }
        // Unconditional: a slower search that this one superseded may have put the progress UI up, and both
        // of these do nothing when there is nothing to take down.
        SetFindBusy(false);
        _findBar.SetSearching(false);
        if (found >= 0)
        {
            GoToLine(found + 1);
            NoteIfMatchIsHidden(found);
            // Held down, the key repeats faster than an idle moment comes round: WM_PAINT and the refresh
            // timer both wait for one, so without this the view and the counts sit still until it is let go.
            UpdateStatus();
            _grid.Update();
            _status.Update();
            _findBar.PaintNow();
        }
        else
        {
            // The same feedback as every other find command. It can say so in the status bar now that the
            // tally has moved to the find bar and is no longer the thing being covered up.
            NoMoreMatches(_doc.IsIndexComplete ? "No more matches" : "No more matches yet",
                _doc.IsIndexComplete
                    ? $"No more matches for {Quote(query.Text)}"
                    : $"No more matches for {Quote(query.Text)} yet \u2014 the file is still loading");
        }
    }

    /// <summary>Says so when a search has landed on a line whose match is inside a field that is not being
    /// shown. The search runs on the whole raw line - deliberately, so nothing is ever unfindable - but
    /// arriving at a line with nothing lit up reads as the search being broken unless it is explained.
    /// It is said beside the search box, which is the one place that survives the next status refresh.</summary>
    private void NoteIfMatchIsHidden(long line)
        => _hiddenMatch = _grid.FindTermIsVisibleOn(line) ? "" : "match is in a hidden field";

    private string _hiddenMatch = "";

    private void RepeatFind(bool forward)
    {
        if (_lastQuery is { } q) { DoFind(q, forward); return; }
        // Nothing active: bring the bar back with whatever was last typed in it and run that, so F3 still
        // repeats a search that was closed - but now with the term visible instead of hidden in a field.
        ShowFind(focus: !_findBar.HasTerm);
        _findBar.Run(forward);
    }

    /// <summary>Drops the find term: highlights off, counts gone, and the sweep behind it released.</summary>
    private void ClearFind()
    {
        _lastQuery = null;
        _hiddenMatch = "";
        // Release the sweep before repainting, not after. The minimap decides whether it has anything to
        // redraw by comparing the hit count it last drew against the document's - so repainting first asks
        // it that question while the hits are all still there, and it sits on them until something else
        // happens to invalidate the view.
        _doc.DropSearch();
        _grid.SetFindHighlight(null);
        _findMsg = "";
        _tally = _tallyDetail = "";
        _tallyLine = -1;
        _findBar.SetMessage("");
        UpdateStatus();
    }

    /// <summary>How long a tally may stand while something behind it is still moving.</summary>
    private static readonly TimeSpan TallyMaxAge = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether the counts need re-reading.
    ///
    /// Two things move underneath them and both have to be watched the same way - while they run the numbers
    /// climb, and when they stop the numbers have to be read one last time or they stand at whatever they
    /// reached a moment before the end. The sweep is one; the filter pass is the other, since what is hidden
    /// decides the shown/hidden split and which match the caret counts as. Hiding is listed apart from the
    /// filters themselves because showing only the filtered lines changes no filter.</summary>
    internal static bool TallyIsStale(bool swept, bool wasSwept, bool settled, bool wasSettled,
                                      bool sameLine, bool sameFilters, bool sameHiding, bool haveText,
                                      TimeSpan age)
        => !haveText || !sameLine || !sameFilters || !sameHiding
           || swept != wasSwept || settled != wasSettled
           || ((!swept || !settled) && age > TallyMaxAge);

    /// <summary>The "Match 12 of 348" text, re-read whenever <see cref="TallyIsStale"/> says so.</summary>
    private string RefreshTally()
    {
        if (_lastQuery is not { } query) return "";
        long caret = _grid.CaretLine;
        bool swept = _doc.FindComplete;
        bool settled = _doc.IsFilterIdle;
        if (!TallyIsStale(swept, _tallySwept, settled, _tallySettled, caret == _tallyLine,
                          _tallyGeneration == _doc.FilterGeneration, _tallyHiding == _doc.FilteredMode,
                          _tally.Length > 0, DateTime.UtcNow - _tallyAt))
            return _tally;

        _tallyLine = caret;
        _tallyGeneration = _doc.FilterGeneration;
        _tallyHiding = _doc.FilteredMode;
        _tallySwept = swept;
        _tallySettled = settled;
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

    private void SetProgress(double fraction)
    {
        _progress.Maximum = 1000;
        int v = (int)Math.Clamp(fraction * 1000, 0, 1000);
        // Set from just above: Windows slides the fill towards a rising value and lags badly behind a fast
        // job, but a step DOWN lands at once. Same reason as the find dialog's bar.
        if (v < _progress.Maximum) { _progress.Value = v + 1; _progress.Value = v; }
        else _progress.Value = v;
    }

    private void GoTo()
    {
        using var dlg = new GoToDialog(_doc.FirstDisplayLine + 1, Math.Max(1, _doc.LastDisplayLine + 1),
                                       Math.Max(1, _grid.CaretLine + 1));
        if (dlg.ShowDialog(this) == DialogResult.OK) GoToLine(dlg.LineNumber);
    }

    private void GoToLine(long oneBased)
    {
        long line = Math.Clamp(oneBased - 1, _doc.FirstDisplayLine, Math.Max(_doc.FirstDisplayLine, _doc.LastDisplayLine));
        _grid.GoToLine(line);
    }

    // ---- crop ----

    /// <summary>Shows only the stretch of the log the selection covers - the first selected line to the last,
    /// everything between them included whether or not it is selected, and whether or not the filters are
    /// currently hiding it. A crop is a stretch of the FILE, so switching the filters off inside one reveals
    /// the lines it always held rather than a different stretch.</summary>
    private void CropToSelection()
    {
        if (!_grid.SelectionBounds(out long first, out long last)) return;
        // Captured before the change, in the row space the change is about to replace - the same dance every
        // filter change does.
        var anchor = _grid.CaptureViewAnchor();
        if (!_doc.SetCrop(first, last + 1)) return;
        _lastCrop = _doc.Crop;
        // Asked for outright, with lines deliberately picked out, so this always takes them: the reader named
        // the stretch and the crop now IS that stretch.
        AfterCropChanged(anchor, TakeSelectionForCrop);
    }

    /// <summary>Goes back to the whole file, or returns to the crop last set. One key does both, because they
    /// are the same question asked twice - and remembering the crop is what saves picking the lines out again
    /// merely to look outside them for a moment.</summary>
    private void ToggleCrop()
    {
        var anchor = _grid.CaptureViewAnchor();
        if (_doc.Crop is not null)
        {
            _doc.ClearCrop();
            AfterCropChanged(anchor, ReturnSelection);
        }
        else
        {
            if (_lastCrop is not { } crop || !_doc.SetCrop(crop.From, crop.ToExclusive)) return;
            AfterCropChanged(anchor, BorrowSelection);
        }
    }

    /// <summary>True while the selection is still the one the crop left behind - so still the crop's to give
    /// back or take away. Any choice of the reader's own ends that, permanently.</summary>
    private bool SelectionStillBorrowed
        => _cropSelection is not null && _cropSelectionVersion == _grid.ChosenVersion;

    /// <summary>Cropping leaves nothing chosen, and remembers what it took so it can be handed back. Called
    /// only for a crop the reader asked for by picking lines out, where taking them is the whole point.</summary>
    private void TakeSelectionForCrop()
    {
        _cropSelection = _grid.CaptureSelectionState();
        _grid.ClearSelectionAndCaret();
        _cropSelectionVersion = _grid.ChosenVersion;
    }

    /// <summary>Re-applying a crop leaves nothing chosen again - but only when the selection is still the one
    /// this handed back. A choice the reader made in between is theirs, and ends the arrangement.</summary>
    private void BorrowSelection()
    {
        if (!SelectionStillBorrowed) { ForgetBorrowedSelection(); return; }
        _grid.ClearSelectionAndCaret();
        _cropSelectionVersion = _grid.ChosenVersion;
    }

    /// <summary>Hands the selection back on the way out of a crop, if it is still the crop's to hand back.</summary>
    private void ReturnSelection()
    {
        if (!SelectionStillBorrowed) { ForgetBorrowedSelection(); return; }
        _grid.RestoreSelectionState(_cropSelection!.Value);
        _cropSelectionVersion = _grid.ChosenVersion;
    }

    private void ForgetBorrowedSelection()
    {
        _cropSelection = null;
        _cropSelectionVersion = -1;
    }

    /// <summary>Puts the view back together around the anchor taken before the crop moved. The rows have all
    /// been renumbered, so the viewport and the caret are re-derived from the LINES they were on.
    /// <para><paramref name="selection"/> runs after the anchor is armed and before the view is laid out
    /// again, so what it says about the caret is what the lay-out uses.</para></summary>
    private void AfterCropChanged(ViewAnchor anchor, Action selection)
    {
        _grid.SetViewAnchor(anchor);
        selection();
        _grid.RefreshView();
        _anchorActive = anchor.IsValid;
        _grid.InvalidateMatchMap();
        _filterTree.RefreshCounts();
        UpdateStatus();
        _status.Update();
        SyncCropMenu();
    }

    /// <summary>Keeps the crop commands in step with the selection.
    /// <para>A menu item's shortcut is dispatched through the item, and a disabled item swallows it - so one
    /// left greyed takes its key down with it. Opening the View menu with nothing selected once would have
    /// killed Ctrl+[ until the menu happened to be opened again with something selected, which is a dead key
    /// and no way of telling why.</para></summary>
    private void SyncCropCommands()
    {
        _miCrop.Enabled = _grid.SelectionBounds(out _, out _);
        _miUncrop.Enabled = _doc.Crop is not null || _lastCrop is not null;
    }

    private void SyncCropMenu()
    {
        SyncCropCommands();
        // One entry, one wording, whichever way it will go. Naming both halves is what says the crop is kept
        // when it is hidden - a label reading only "Hide Crop" would leave re-applying it undiscoverable.
        _miUncrop.ToolTipText = _doc.Crop is not null
            ? "Go back to the whole file. The crop is kept, so Ctrl+] brings it back."
            : $"Show lines {(_lastCrop?.From ?? 0) + 1:N0}\u2013{_lastCrop?.ToExclusive ?? 0:N0} again.";
    }

    /// <summary>Draws the crop in the middle of the menu bar. ToolStrip has no notion of centring, so the item
    /// is given the left margin that puts it there - measured against the whole bar rather than the space left
    /// over, because the middle of the window is where the eye goes and it must not drift as menu text
    /// changes.
    /// <para>Worked out from where the item actually LANDED rather than from what it should measure: a strip
    /// adds padding of its own between and around its items, and guessing at that put the chip a good forty
    /// pixels off centre. Correcting by the error instead is exact whatever the padding turns out to be, and
    /// settles in one step. Clamped so it can never ride over the last menu or under the update notice.</para></summary>
    private void CentreCropLabel()
    {
        if (MainMenuStrip is not { } menu || !_cropLabel.Visible) return;
        menu.PerformLayout();

        int width = _cropLabel.Width;
        if (width <= 0) return;

        int menusEnd = 0;
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripMenuItem { Available: true } m) menusEnd = Math.Max(menusEnd, m.Bounds.Right);

        int gap = Dpi(16);
        int room = menu.ClientSize.Width - (_updateLabel.Visible ? _updateLabel.Width + gap : 0) - width;
        int want = Math.Clamp((menu.ClientSize.Width - width) / 2, menusEnd + gap, Math.Max(menusEnd + gap, room));

        int delta = want - _cropLabel.Bounds.Left;
        if (Math.Abs(delta) <= 1) return;
        int left = Math.Max(0, _cropLabel.Margin.Left + delta);
        if (left == _cropLabel.Margin.Left) return;
        _cropLabel.Margin = new Padding(left, 0, 0, 0);
        menu.PerformLayout();
    }

    private void UpdateCropLabel()
    {
        if (_doc.Crop is not { } crop)
        {
            if (_cropLabel.Visible) { _cropLabel.Visible = false; _cropLabel.Text = ""; }
            return;
        }

        long rows = _doc.DisplayLineCount;
        string text = $"Cropped to {_doc.FirstDisplayLine + 1:N0}\u2013{_doc.LastDisplayLine + 1:N0}"
                    + $"  \u00B7  {rows:N0} {(rows == 1 ? "line" : "lines")}   \u00D7";
        bool changed = _cropLabel.Text != text;
        if (changed) _cropLabel.Text = text;
        if (!_cropLabel.Visible)
        {
            _cropLabel.Visible = true;
            changed = true;
        }
        _cropLabel.ToolTipText = $"Showing {rows:N0} of {_doc.CompletedLineCount:N0} lines.\n"
                               + "Click to see the whole file again \u00B7 Ctrl+] toggles the crop";
        if (changed) CentreCropLabel();
    }

    // ---- status ----

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!OfferToSaveFilters()) { e.Cancel = true; return; }
        _saveCts?.Cancel();
        _refreshTimer.Stop();
        // Nothing beats once the timer is down, so the watchdog would call an ordinary shutdown a hang.
        _watchdog?.Dispose();
        _watchdog = null;
        _settingsDirty = _stateDirty = true;   // a clean exit rewrites both, as it always has
        FlushConfig(force: true);
        Hide();
    }

    /// <summary>Asks about unsaved filter changes before something throws them away, and saves them if that
    /// is the answer. Returns false for "don't do it after all". One method rather than one per caller, so
    /// closing the window and closing the filters cannot come to disagree about the question or about when
    /// it is worth asking.</summary>
    private bool OfferToSaveFilters()
    {
        if (!ShouldOfferToSaveFilters(NoSavePrompt, _filtersDirty, _filterFilePath)) return true;
        var r = AnswerSavePromptForTesting?.Invoke()
                ?? MessageBox.Show(this, "Save changes to filters?", "Cascade", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        // "Yes" that did not reach disk - the file dialog was dismissed, or the write failed - is not
        // permission to throw the changes away.
        return r != DialogResult.Yes || SaveFilters(false);
    }

    /// <summary>When unsaved filter changes are worth asking about. Static so the rule can be read without a
    /// modal prompt standing in the way. Nothing is asked when there is no file to save to: the answer would
    /// have to be a file dialog, which is a great deal to put in the way of closing a window.</summary>
    internal static bool ShouldOfferToSaveFilters(bool suppressed, bool dirty, string? filterFilePath)
        => !suppressed && dirty && filterFilePath is not null;

    /// <summary>What to answer the save prompt with. Set only by checks - a modal message box in a headless
    /// run has nobody to answer it. A field, not a property, because the WinForms analyser objects to
    /// properties on a Control.</summary>
    internal Func<DialogResult>? AnswerSavePromptForTesting;

    internal string? FilterFileForTesting => _filterFilePath;
    internal bool FiltersAreDirtyForTesting => _filtersDirty;
    internal bool WatchingForHangsForTesting => _watchdog is not null;
    internal void LoadFiltersForTesting(string path) => LoadFiltersFrom(path);

    // Releasing the mapping of a very large log means the kernel has to give back every page of it that is
    // resident - two thirds of a second for a seven gigabyte trace, on the thread that draws. WinForms
    // disposes a top-level form while its window is still up, so the window is taken down first (above) and
    // the reader sees the app go at once. The work itself is unavoidable: the address space has to be torn
    // down either way, whether we ask or the process simply ends.
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _watchdog?.Dispose(); _doc.Dispose(); }
        base.Dispose(disposing);
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
            || _findBusy || _findMsg.Length > 0 || _lastQuery is not null || _saveCts is not null
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
        structural |= EnsureElapsedSlot();
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

        bool exporting = _saveCts is not null;
        bool showBar = _findBusy || exporting || indexing || filtering;
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
            SetProgress(fraction);
        }
        else if (exporting)
        {
            SetActivity($"Saving\u2026 {_saveFraction * 100:F0}%  (Esc)", SystemColors.ControlText, _saveWhat);
            SetProgress(_saveFraction);
        }
        else if (_findMsg.Length > 0)
        {
            SetActivity(_findMsg, Color.Firebrick, _findMsgDetail);
            if (showBar) SetProgress(Fraction(indexing, filtering));
        }
        else if (indexing)
        {
            // Lines cannot be counted before the scan ends, but bytes can: the file's size is known up
            // front, so this is a real fraction rather than a barber's pole.
            double done = Fraction(indexing, filtering);
            SetActivity($"Indexing\u2026 {done * 100:F0}%", SystemColors.ControlText,
                $"Indexing\u2026 {_doc.CompletedLineCount:N0} lines so far");
            SetProgress(done);
        }
        else if (filtering)
        {
            double done = Fraction(indexing, filtering);
            SetActivity($"Filtering\u2026 {done * 100:F0}%", SystemColors.ControlText,
                $"Filtering\u2026 {_doc.FilterProcessedLineCount:N0} of {_doc.CompletedLineCount:N0} lines");
            SetProgress(done);
        }
        else
        {
            SetActivity("", SystemColors.ControlText);
        }

        // The count of what the term matched belongs beside the term, not at the far corner of the window.
        string tally = RefreshTally();
        if (_hiddenMatch.Length > 0)
            _findBar.SetMessage(tally.Length > 0 ? $"{tally} \u2014 {_hiddenMatch}" : _hiddenMatch,
                "The search runs on the whole line, including the fields you have hidden, so this line "
                + "matches even though nothing on it is lit up.");
        else
            _findBar.SetMessage(tally, _tallyDetail);

        double Fraction(bool ix, bool ft)
        {
            if (ix) return _doc.IndexedFraction;
            if (!ft) return 0;
            long total = Math.Max(1, _doc.CompletedLineCount);
            return Math.Clamp(_doc.FilterProcessedLineCount / (double)total, 0, 1);
        }

        _selLabel.Text = $"Sel: {_grid.SelectedCount:N0}";
        UpdateElapsed();
        _filLabel.Text = $"Fil: {_doc.MatchedLineCount:N0}";
        _totalLabel.Text = $"Total: {_doc.DisplayLineCount:N0}";
        _totalLabel.ToolTipText = _doc.Crop is null
            ? null
            : $"Lines in the crop. The file has {_doc.CompletedLineCount:N0}.";
        UpdateCropLabel();
        _showLabel.Text = _doc.FilteredMode ? ShowingMatchesOnly : ShowingAllLines;
        _showLabel.ToolTipText = _doc.FilteredMode
            ? "Only lines a filter matched are shown (Ctrl+H shows them all)"
            : "Every line is shown, with the matches coloured (Ctrl+H hides the rest)";
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

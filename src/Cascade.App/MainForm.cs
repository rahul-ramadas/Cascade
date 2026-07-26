using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;
using Cascade.Core.Persistence;

namespace Cascade.App;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly CascadeDocument _doc = new();
    private readonly LineGridControl _grid = new() { Dock = DockStyle.Fill };
    private readonly FilterTreeControl _filterTree = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _srcLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _busyLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _selLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _filLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _totalLabel = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _zoomLabel = new() { AutoSize = true };
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 30, AutoSize = false, Width = 120 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 33 };

    private ToolStripMenuItem _miFilteredMode = null!, _miLineNumbers = null!;
    private ToolStripMenuItem _recentFilesMenu = null!, _recentFilterFilesMenu = null!;

    private FindDialog? _findDialog;
    private FindQuery? _lastQuery;
    private string? _filterFilePath;
    private bool _filtersDirty;
    private volatile bool _pendingRefresh;
    private long _lastRowCount = -1, _lastMatched = -1;
    private bool _lastBusy;
    private bool _anchorActive;
    private int _treePanel = 2; // which split panel holds the filter tree (for show/hide)

    private enum FilterDock { Bottom, Top, Left, Right }

    public MainForm(AppSettings settings, string[] args)
    {
        _settings = settings;
        Text = "Cascade";
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(700, 400);
        try { Icon = SystemIcons.Application; } catch { /* ignore */ }

        BuildMenu();
        BuildStatusBar();

        _split.Panel1.Controls.Add(_grid);
        _split.Panel2.Controls.Add(_filterTree);
        Controls.Add(_split);
        Controls.Add(_status);
        _split.BringToFront();

        _grid.Attach(_doc, _settings);
        _filterTree.Attach(_doc);
        _filterTree.SetSettings(_settings);

        _doc.Updated += () => _pendingRefresh = true;
        _grid.SelectionChanged += UpdateStatus;
        _grid.ZoomChanged += UpdateStatus;
        _grid.LineDoubleClicked += CreateFilterFromLine;
        _filterTree.FiltersChanged += OnFiltersChanged;
        _filterTree.EditRequested += EditFilter;
        _filterTree.AddRequested += AddFilter;
        _filterTree.FindFilterRequested += FindFilterMatch;

        _refreshTimer.Tick += (_, _) =>
        {
            if (_pendingRefresh) { _pendingRefresh = false; _grid.RefreshView(); _filterTree.RefreshCounts(); }
            else if (_doc.IsBusy) _filterTree.RefreshCounts();
            if (_anchorActive && !_doc.IsBusy) { _grid.RefreshView(); _grid.ClearViewAnchor(); _anchorActive = false; }
            UpdateStatusIfChanged();
        };
        _refreshTimer.Start();

        Shown += (_, _) => ProcessArgs(args);
        FormClosing += OnClosing;
        UpdateStatus();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try { _split.SplitterDistance = (int)(ClientSize.Height * 0.7); } catch { /* size not ready */ }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.L)) { ToggleFilterList(); return true; }
        if (!IsTextInputFocused())
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Up: SetFilterDock(FilterDock.Top); return true;
                case Keys.Control | Keys.Down: SetFilterDock(FilterDock.Bottom); return true;
                case Keys.Control | Keys.Left: SetFilterDock(FilterDock.Left); return true;
                case Keys.Control | Keys.Right: SetFilterDock(FilterDock.Right); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- menu ----

    private static ToolStripMenuItem Mi(string text, EventHandler onClick, Keys keys = Keys.None)
    {
        var m = new ToolStripMenuItem(text, null, onClick);
        if (keys != Keys.None) m.ShortcutKeys = keys;
        return m;
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Mi("&Open…", (_, _) => OpenFileDialogAndLoad(), Keys.Control | Keys.O));
        file.DropDownItems.Add(Mi("&Reload", (_, _) => Reload(), Keys.F5));
        file.DropDownItems.Add(Mi("Open from &Clipboard", (_, _) => OpenFromClipboard()));
        file.DropDownItems.Add(Mi("Save Current &Lines…", (_, _) => SaveCurrentLines()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Mi("&Load Filters…", (_, _) => LoadFilters()));
        file.DropDownItems.Add(Mi("&Save Filters", (_, _) => SaveFilters(false), Keys.Control | Keys.S));
        file.DropDownItems.Add(Mi("Save Filters &As…", (_, _) => SaveFilters(true)));
        file.DropDownItems.Add(Mi("&Append Filters…", (_, _) => AppendFilters()));
        file.DropDownItems.Add(Mi("&Import .tat filters…", (_, _) => ImportTat()));
        file.DropDownItems.Add(new ToolStripSeparator());
        _recentFilesMenu = new ToolStripMenuItem("Recent &Files");
        _recentFilterFilesMenu = new ToolStripMenuItem("Recent Filter Files");
        file.DropDownItems.Add(_recentFilesMenu);
        file.DropDownItems.Add(_recentFilterFilesMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Mi("E&xit", (_, _) => Close()));

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(Mi("&Copy", (_, _) => _grid.CopySelection(false), Keys.Control | Keys.C));
        edit.DropDownItems.Add(Mi("Copy with Line &Numbers", (_, _) => _grid.CopySelection(true)));
        edit.DropDownItems.Add(Mi("Select &All", (_, _) => _grid.SelectAll(), Keys.Control | Keys.A));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Mi("&Find…", (_, _) => ShowFind(), Keys.Control | Keys.F));
        edit.DropDownItems.Add(Mi("Find &Next", (_, _) => RepeatFind(true), Keys.F3));
        edit.DropDownItems.Add(Mi("Find &Previous", (_, _) => RepeatFind(false), Keys.Shift | Keys.F3));
        edit.DropDownItems.Add(Mi("&Go To Line…", (_, _) => GoTo(), Keys.Control | Keys.G));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Mi("&Preferences…", (_, _) => ShowPreferences()));

        var view = new ToolStripMenuItem("&View");
        _miFilteredMode = Mi("Show Only &Filtered Lines", (_, _) => ToggleFilteredMode(), Keys.Control | Keys.H);
        _miLineNumbers = new ToolStripMenuItem("Show &Line Numbers", null, (_, _) =>
        {
            _settings.ShowLineNumbers = !_settings.ShowLineNumbers;
            _miLineNumbers.Checked = _settings.ShowLineNumbers;
            _grid.RefreshView();
        })
        { Checked = _settings.ShowLineNumbers };
        view.DropDownItems.Add(_miFilteredMode);
        view.DropDownItems.Add(_miLineNumbers);
        view.DropDownItems.Add(BuildMarkersMenu());
        view.DropDownItems.Add(Mi("&Columns…", (_, _) => ShowColumns()));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Mi("Zoom &In", (_, _) => _grid.Zoom(10), Keys.Control | Keys.Oemplus));
        view.DropDownItems.Add(Mi("Zoom &Out", (_, _) => _grid.Zoom(-10), Keys.Control | Keys.OemMinus));
        view.DropDownItems.Add(Mi("&Reset Zoom", (_, _) => _grid.ResetZoom(), Keys.Control | Keys.D0));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Mi("Focus &Text Area", (_, _) => FocusTextArea(), Keys.Control | Keys.Shift | Keys.T));
        view.DropDownItems.Add(Mi("Focus Filter &List", (_, _) => FocusFilterList(), Keys.Control | Keys.Shift | Keys.F));
        view.DropDownItems.Add(Mi("Focus Filter &Search", (_, _) => FocusFilterSearch(), Keys.Control | Keys.E));
        view.DropDownItems.Add(BuildFilterLocationMenu());
        view.DropDownItems.Add(BuildEncodingMenu());

        var filters = new ToolStripMenuItem("Fi&lters");
        filters.DropDownItems.Add(Mi("&Add Filter…", (_, _) => AddFilter(null)));
        filters.DropDownItems.Add(Mi("Add &Child Filter…", (_, _) => AddFilter(_filterTree.SelectedFilter)));
        filters.DropDownItems.Add(Mi("New Filter from Current &Line…", (_, _) => NewFilterFromCurrentLine(), Keys.Control | Keys.Shift | Keys.N));
        filters.DropDownItems.Add(Mi("&Edit Filter…", (_, _) => { if (_filterTree.SelectedFilter is { } f) EditFilter(f); }));
        filters.DropDownItems.Add(Mi("&Remove Filter", (_, _) => _filterTree.RemoveSelected()));
        filters.DropDownItems.Add(new ToolStripSeparator());
        filters.DropDownItems.Add(Mi("Find &Next Match", (_, _) => FindSelectedFilterMatch(true), Keys.F4));
        filters.DropDownItems.Add(Mi("Find Pre&vious Match", (_, _) => FindSelectedFilterMatch(false), Keys.Shift | Keys.F4));
        filters.DropDownItems.Add(new ToolStripSeparator());
        filters.DropDownItems.Add(Mi("Enable All", (_, _) => _filterTree.SetAllEnabled(true)));
        filters.DropDownItems.Add(Mi("Disable All", (_, _) => _filterTree.SetAllEnabled(false)));
        filters.DropDownItems.Add(Mi("Remove All", (_, _) => _filterTree.RemoveAll()));
        filters.DropDownItems.Add(new ToolStripSeparator());
        filters.DropDownItems.Add(Mi("&Find Filter", (_, _) => _filterTree.FocusSearch()));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Mi("&About Cascade", (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, filters, help });
        MainMenuStrip = menu;
        Controls.Add(menu);
        RefreshRecentMenus();
    }

    private ToolStripMenuItem BuildMarkersMenu()
    {
        var m = new ToolStripMenuItem("Show &Markers");
        void Item(string text, MarkerVisibilityMode mode) =>
            m.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => { _settings.MarkerVisibility = mode; _grid.RefreshView(); }) { Checked = _settings.MarkerVisibility == mode });
        Item("Always", MarkerVisibilityMode.Always);
        Item("Never", MarkerVisibilityMode.Never);
        Item("When in use", MarkerVisibilityMode.WhenInUse);
        return m;
    }

    private ToolStripMenuItem BuildFilterLocationMenu()
    {
        var m = new ToolStripMenuItem("Filter List &Location");
        m.DropDownItems.Add("Dock &Bottom\tCtrl+Down", null, (_, _) => SetFilterDock(FilterDock.Bottom));
        m.DropDownItems.Add("Dock &Top\tCtrl+Up", null, (_, _) => SetFilterDock(FilterDock.Top));
        m.DropDownItems.Add("Dock &Left\tCtrl+Left", null, (_, _) => SetFilterDock(FilterDock.Left));
        m.DropDownItems.Add("Dock &Right\tCtrl+Right", null, (_, _) => SetFilterDock(FilterDock.Right));
        m.DropDownItems.Add(new ToolStripSeparator());
        m.DropDownItems.Add("Show/&Hide Filter List\tCtrl+Shift+L", null, (_, _) => ToggleFilterList());
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
        _status.Items.AddRange(new ToolStripItem[]
        {
            _srcLabel, _progress, _busyLabel,
            new ToolStripStatusLabel("Sel:"), _selLabel,
            new ToolStripStatusLabel("Fil:"), _filLabel,
            new ToolStripStatusLabel("Total:"), _totalLabel,
            _zoomLabel
        });
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
            _settings.AddRecentFile(path);
            RefreshRecentMenus();
            Text = $"Cascade — {Path.GetFileName(path)}";
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
        _filtersDirty = true;
        long anchor = _grid.CurrentAnchorLine();
        _doc.ApplyFilters();
        // In filtered mode the matched rows shift, so re-select the anchor line; in dim mode the row
        // set is unchanged, so leave any existing (possibly multi-row) selection intact.
        _grid.SetViewAnchor(anchor, select: _doc.FilteredMode);
        _grid.RefreshView();
        _anchorActive = anchor >= 0;
        UpdateTitle();
        UpdateStatus();
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
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _doc.Filters.Add(filter, parent);
            _filterTree.Rebuild();
            OnFiltersChanged();
        }
    }

    private void EditFilter(Filter filter)
    {
        using var dlg = new FilterEditDialog(filter, isNew: false);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _filterTree.Rebuild();
            OnFiltersChanged();
        }
    }

    private void CreateFilterFromLine(long line)
    {
        string text = _doc.GetLineText(line).Trim();
        if (text.Length > 200) text = text[..200];
        var filter = new Filter { Enabled = true, Match = { Text = text } };
        using var dlg = new FilterEditDialog(filter, isNew: true);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _doc.Filters.Add(filter);
            _filterTree.Rebuild();
            OnFiltersChanged();
        }
    }

    private void NewFilterFromCurrentLine()
    {
        long line = _grid.CaretLine;
        if (line < 0) { MessageBox.Show(this, "Select a line in the text view first.", "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        CreateFilterFromLine(line);
    }

    private void FindSelectedFilterMatch(bool forward)
    {
        if (_filterTree.SelectedFilter is { } f) FindFilterMatch(f, forward);
    }

    private void FindFilterMatch(Filter filter, bool forward)
    {
        if (string.IsNullOrEmpty(_doc.FilePath)) return;
        long caret = _grid.CaretLine;
        long start = caret < 0 ? (forward ? 0 : _doc.CompletedLineCount - 1) : caret + (forward ? 1 : -1);
        long found = _doc.FindLineMatchingFilter(filter, start, forward, CancellationToken.None);
        if (found >= 0) GoToLine(found + 1);
        else System.Media.SystemSounds.Beep.Play();
    }

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
                _settings.AddRecentFilterFile(path);
            }
            _filtersDirty = false;
            _filterTree.Attach(_doc);
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
            foreach (var root in loaded.Roots.ToList()) _doc.Filters.Add(root.Clone());
            _filtersDirty = true;
            _filterTree.Attach(_doc);
            OnFiltersChanged();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ImportTat()
    {
        using var dlg = new OpenFileDialog { Filter = "TAT filters (*.tat)|*.tat|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadFiltersFrom(dlg.FileName);
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
            _settings.AddRecentFilterFile(path);
            RefreshRecentMenus();
            UpdateTitle();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cascade", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ---- view ----

    private void ToggleFilteredMode()
    {
        _doc.Filters.ShowOnlyFilteredLines = !_doc.Filters.ShowOnlyFilteredLines;
        SyncFilteredModeMenu();
        long anchor = _grid.CurrentAnchorLine();
        _doc.ApplyFilters();
        _grid.SetViewAnchor(anchor, select: true);
        _grid.RefreshView();
        _anchorActive = anchor >= 0;
        UpdateStatus();
    }

    private void SyncFilteredModeMenu() => _miFilteredMode.Checked = _doc.Filters.ShowOnlyFilteredLines;

    private void SetFilterDock(FilterDock dock)
    {
        _split.SuspendLayout();
        _split.Panel1Collapsed = false;
        _split.Panel2Collapsed = false;
        _split.Panel1.Controls.Clear();
        _split.Panel2.Controls.Clear();

        bool treeFirst = dock is FilterDock.Top or FilterDock.Left;
        _split.Orientation = dock is FilterDock.Left or FilterDock.Right ? Orientation.Vertical : Orientation.Horizontal;
        if (treeFirst) { _split.Panel1.Controls.Add(_filterTree); _split.Panel2.Controls.Add(_grid); _treePanel = 1; }
        else { _split.Panel1.Controls.Add(_grid); _split.Panel2.Controls.Add(_filterTree); _treePanel = 2; }

        int total = _split.Orientation == Orientation.Vertical ? _split.Width : _split.Height;
        int treeSize = Math.Max(60, (int)(total * 0.3));
        try { _split.SplitterDistance = treeFirst ? treeSize : Math.Max(1, total - treeSize - _split.SplitterWidth); }
        catch { /* sizes not ready */ }
        _split.ResumeLayout();
    }

    private void ToggleFilterList()
    {
        if (_treePanel == 1) _split.Panel1Collapsed = !_split.Panel1Collapsed;
        else _split.Panel2Collapsed = !_split.Panel2Collapsed;
    }

    private void FocusTextArea() => _grid.Focus();

    private void FocusFilterList() { EnsureFilterListVisible(); _filterTree.FocusList(); }

    private void FocusFilterSearch() { EnsureFilterListVisible(); _filterTree.FocusSearch(); }

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
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _miLineNumbers.Checked = _settings.ShowLineNumbers;
            _grid.ApplySettings(_settings);
            _filterTree.SetSettings(_settings);
            UpdateStatus();
        }
    }

    // ---- find / goto ----

    private void ShowFind()
    {
        _findDialog ??= new FindDialog(DoFind);
        if (!_findDialog.Visible) _findDialog.Show(this);
        _findDialog.FocusInput();
    }

    private void DoFind(FindQuery query, bool forward)
    {
        _lastQuery = query;
        long start = _grid.CaretLine;
        start = start < 0 ? 0 : start + (forward ? 1 : -1);
        long found = _doc.FindLine(query, start, forward, CancellationToken.None);
        if (found >= 0) { GoToLine(found + 1); _findDialog?.SetStatus(""); }
        else _findDialog?.SetStatus(_doc.IsIndexComplete ? "Not found." : "Not found yet — file still loading…");
    }

    private void RepeatFind(bool forward) { if (_lastQuery is { } q) DoFind(q, forward); else ShowFind(); }

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
        if (_filtersDirty && _filterFilePath is not null)
        {
            var r = MessageBox.Show(this, "Save changes to filters?", "Cascade", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes) SaveFilters(false);
        }
        _refreshTimer.Stop();
        _settings.Save();
        _doc.Dispose();
    }

    private void UpdateStatusIfChanged()
    {
        if (_doc.RowCount != _lastRowCount || _doc.MatchedLineCount != _lastMatched
            || _doc.IsBusy || _doc.IsBusy != _lastBusy)
            UpdateStatus();
    }

    private void UpdateStatus()
    {
        _lastRowCount = _doc.RowCount;
        _lastMatched = _doc.MatchedLineCount;
        _lastBusy = _doc.IsBusy;
        _srcLabel.Text = string.IsNullOrEmpty(_doc.FilePath) ? "  (no file)" : "  " + _doc.FilePath;

        bool hasFile = !string.IsNullOrEmpty(_doc.FilePath);
        bool indexing = hasFile && !_doc.IsIndexComplete;
        bool filtering = hasFile && _doc.IsIndexComplete && !_doc.IsFilterIdle;

        _busyLabel.Text = indexing ? $"  \u23f3 Indexing\u2026 {_doc.CompletedLineCount:N0}  "
            : filtering ? $"  \u23f3 Filtering\u2026 {_doc.FilterProcessedLineCount:N0}  " : "";

        _progress.Visible = _doc.IsBusy;
        if (indexing)
        {
            // Total line count is unknown until indexing finishes, so animate indeterminately.
            if (_progress.Style != ProgressBarStyle.Marquee) _progress.Style = ProgressBarStyle.Marquee;
        }
        else if (filtering)
        {
            if (_progress.Style != ProgressBarStyle.Continuous) _progress.Style = ProgressBarStyle.Continuous;
            long total = Math.Max(1, _doc.CompletedLineCount);
            long done = Math.Clamp(_doc.FilterProcessedLineCount, 0, total);
            _progress.Maximum = 1000;
            _progress.Value = (int)(1000L * done / total);
        }

        _selLabel.Text = _grid.SelectedCount.ToString("N0");
        _filLabel.Text = _doc.MatchedLineCount.ToString("N0");
        _totalLabel.Text = _doc.CompletedLineCount.ToString("N0");
        _zoomLabel.Text = $"{_settings.ZoomPercent}%";
    }

    private void UpdateTitle()
    {
        string file = string.IsNullOrEmpty(_doc.FilePath) ? "" : " — " + Path.GetFileName(_doc.FilePath);
        string filt = _filterFilePath is not null ? $" [{Path.GetFileName(_filterFilePath)}{(_filtersDirty ? " *" : "")}]" : (_filtersDirty ? " [filters *]" : "");
        Text = "Cascade" + file + filt;
    }

    private void RefreshRecentMenus()
    {
        void Fill(ToolStripMenuItem menu, List<string> items, Action<string> open)
        {
            menu.DropDownItems.Clear();
            foreach (var p in items) menu.DropDownItems.Add(p, null, (_, _) => { if (File.Exists(p)) open(p); });
            menu.Enabled = items.Count > 0;
        }
        Fill(_recentFilesMenu, _settings.RecentFiles, p => OpenFile(p, null));
        Fill(_recentFilterFilesMenu, _settings.RecentFilterFiles, LoadFiltersFrom);
    }

    private void ShowAbout()
        => MessageBox.Show(this,
            "Cascade\nA fast, hierarchical-filtering log/text analyzer.\n\n" +
            "Memory-mapped streaming load · hierarchical filters · columns · markers.\n.NET 10 · WinForms/GDI.",
            "About Cascade", MessageBoxButtons.OK, MessageBoxIcon.Information);
}

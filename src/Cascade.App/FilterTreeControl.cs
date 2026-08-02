using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Document;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// The hierarchical filter list: a checkbox tree with drag-and-drop reordering AND nesting, plus
/// type-to-search (jump/cycle, bold matches) that never hides nodes. Enabling/disabling a filter or
/// changing the tree raises <see cref="FiltersChanged"/>; the host re-applies filters.
/// </summary>
public sealed class FilterTreeControl : UserControl
{

    // The placeholder uses the native Win32 cue banner (see CueTextBox), NOT WinForms' PlaceholderText:
    // the managed PlaceholderText is redrawn in WM_PAINT and flickers on the box's hover/paint cycles,
    // whereas the OS cue banner is painted by the edit control itself and never flickers.
    private readonly CueTextBox _search = new()
    {
        Dock = DockStyle.Top,
        AccessibleName = "Filter search",
        Cue = "Find filter (Enter = next, Shift+Enter = previous)\u2026"
    };
    private readonly BufferedTreeView _tree = new()
    {
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        HideSelection = false,
        FullRowSelect = false,
        DrawMode = TreeViewDrawMode.OwnerDrawText,
        AllowDrop = true,
        ShowLines = true,
        ShowNodeToolTips = true,   // the only way to read a pattern too long for the column
        BorderStyle = BorderStyle.None // client origin matches the header so column dividers line up
    };
    private readonly FilterListHeader _header = new() { Dock = DockStyle.Top };

    private AppSettings _settings = new();
    private Font? _fBase;
    private Font _fReg = null!, _fBold = null!, _fItalic = null!, _fBoldItalic = null!;

    private CascadeDocument? _doc;
    private bool _building;
    private bool _tooltipsQueued;
    private int _nodesBuilt;
    private readonly List<TreeNode> _flat = new();
    private readonly HashSet<string> _collapsed = new(); // filter ids the user has collapsed

    private TreeNode? _dragNode;
    private (Filter? Parent, int Index)? _dragOrigin;
    private int _dragGrabX;
    private int _dragGrabLevel;
    private Point _dragPoint;
    private TreeNode? _pressed;
    private Point _pressedAt;
    private readonly System.Windows.Forms.Timer _autoScroll = new() { Interval = 60 };
    private int _autoScrollStep;
    private bool _editPending;

    public event Action? FiltersChanged;
    public event Action<Filter>? EditRequested;
    public event Action<Filter?>? AddRequested;
    public event Action<Filter, bool>? FindFilterRequested; // (filter, forward)

    /// <summary>Raised with a label for the menu ("Remove Filter") immediately before the list changes the
    /// tree, so the host can snapshot it for undo. Raised speculatively - a move that turns out to be
    /// illegal, or a drag the user cancels, still announces itself and simply changes nothing.</summary>
    public event Action<string>? BeforeFiltersEdited;

    /// <summary>Raised with the search term when a filter search runs off the end of the list. The host
    /// decides how to report it, so all the find commands give identical feedback.</summary>
    public event Action<string>? NoFilterMatch;

    public FilterTreeControl()
    {
        var menu = BuildContextMenu();
        _tree.ContextMenuStrip = menu;

        Controls.Add(_tree);
        Controls.Add(_header);
        Controls.Add(_search);

        // The columns are sized from the rows' content, and the tree's client width shrinks when a vertical
        // scrollbar appears, so any size change means measuring again.
        _tree.ClientSizeChanged += (_, _) => LayoutColumns();
        _tree.HandleCreated += (_, _) => QueueTooltipUpdate();

        _tree.AfterCheck += OnAfterCheck;
        _tree.AfterExpand += (_, e) => { if (!_building && e.Node?.Tag is Filter f) _collapsed.Remove(f.Id); };
        _tree.AfterCollapse += (_, e) => { if (!_building && e.Node?.Tag is Filter f) _collapsed.Add(f.Id); };
        _tree.NodeMouseDoubleClick += (_, e) => { if (e.Node is not null) HandleDoubleClick(e.Node, e.X); };
        _tree.MouseDown += OnTreeMouseDown;
        _tree.MouseMove += OnTreeMouseMove;
        _tree.MouseUp += (_, _) => _pressed = null;
        _tree.KeyDown += OnTreeKeyDown;
        _tree.DrawNode += OnDrawNode;
        _tree.DragEnter += (_, e) => e.Effect = DragDropEffects.Move;
        _tree.DragOver += OnDragOver;
        _tree.DragDrop += OnDragDrop;
        _tree.DragLeave += (_, _) => StopAutoScroll();
        // On the source, not the tree: DoDragDrop is called on this control, so this is where Escape lands.
        QueryContinueDrag += (_, e) => { if (e.EscapePressed) { CancelDrag(); e.Action = DragAction.Cancel; } };
        // Re-place as it scrolls, so holding at the edge carries the filter along instead of just moving
        // the view past it. The pointer has not moved, so the last one it reported is where it still is.
        _autoScroll.Tick += (_, _) => AutoScrollTick();

        _search.TextChanged += (_, _) => JumpToMatch(fromSelection: false, forward: true, announce: false);
        _search.KeyDown += OnSearchKeyDown;

        // A subtle left accent bar marks which sub-area (search box vs list) holds focus; the reserved
        // left padding gives it room without overlapping the controls.
        BackColor = SystemColors.Window;
        DoubleBuffered = true;
        Padding = new Padding(FocusBarWidth, 0, 0, 0);
        _search.GotFocus += (_, _) => Invalidate();
        _search.LostFocus += (_, _) => Invalidate();
        _tree.GotFocus += (_, _) => Invalidate();
        _tree.LostFocus += (_, _) => Invalidate();
    }

    private const int FocusBarWidth = 3;

    /// <summary>Draws the focus accent bar in the reserved left strip, aligned with the focused sub-area.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int w = Padding.Left;
        if (w <= 0) return;
        Rectangle r;
        if (_search.Focused) r = new Rectangle(0, _search.Top, w, _search.Height);
        else if (_tree.Focused) r = new Rectangle(0, _tree.Top, w, _tree.Height);
        else return;
        using var b = new SolidBrush(_settings.SelectionBack);
        e.Graphics.FillRectangle(b, r);
    }

    private void OnTreeMouseDown(object? sender, MouseEventArgs e)
    {
        // FullRowSelect is off, so make the whole colored row clickable for selection.
        var node = _tree.GetNodeAt(0, e.Y);
        if (node is not null && !ReferenceEquals(_tree.SelectedNode, node)) _tree.SelectedNode = node;

        // Anywhere in the row's content can pick the filter up, blank space included. Left of that is the
        // checkbox and the expander, where a press has to keep meaning tick and unfold.
        _pressed = e.Button == MouseButtons.Left && node is not null && e.X >= ContentLeft(node) ? node : null;
        _pressedAt = e.Location;
    }

    /// <summary>Where the row's own content starts: just right of the checkbox, never over it. The paint
    /// and the grab have to agree on this or the row would be draggable somewhere it does not look it.</summary>
    private static int ContentLeft(TreeNode n) => n.Bounds.Left + 2;

    /// <summary>Only the row's own content opens the editor. The checkbox and the expander to its left have
    /// jobs of their own, and clicking either twice means doing that twice - not "edit this".</summary>
    private void HandleDoubleClick(TreeNode node, int x)
    {
        if (x >= ContentLeft(node) && node.Tag is Filter f) EditRequested?.Invoke(f);
    }

    public Filter? SelectedFilter => _tree.SelectedNode?.Tag as Filter;

    public void Attach(CascadeDocument doc)
    {
        _doc = doc;
        Rebuild();
    }

    public void SetSettings(AppSettings settings)
    {
        _settings = settings;
        MeasureDescriptions();
        MeasureCounts();
        _tree.Invalidate();
    }

    public void Rebuild()
    {
        if (_doc is null) return;
        _building = true;
        _tree.BeginUpdate();
        string? selectedId = SelectedFilter?.Id;
        string? topId = (_tree.TopNode?.Tag as Filter)?.Id;
        _tree.Nodes.Clear();
        _flat.Clear();
        foreach (var f in _doc.Filters.Roots) _tree.Nodes.Add(BuildNode(f));
        FlattenInto(_tree.Nodes);
        _tree.EndUpdate();
        _building = false;

        // A deleted filter must not leave its id behind in the collapsed set.
        if (_collapsed.Count > 0) _collapsed.IntersectWith(_flat.Select(n => (n.Tag as Filter)?.Id ?? ""));

        if (selectedId is not null)
            _tree.SelectedNode = _flat.FirstOrDefault(n => (n.Tag as Filter)?.Id == selectedId);

        // Keep the scroll position stable across rebuilds (e.g. drag-drop reparenting).
        if (topId is not null)
        {
            var top = _flat.FirstOrDefault(n => (n.Tag as Filter)?.Id == topId);
            if (top is not null) _tree.TopNode = top;
        }

        MeasureDescriptions();
        MeasureCounts();
    }

    private TreeNode BuildNode(Filter f)
    {
        var node = new TreeNode(f.Match.ToDisplayString()) { Tag = f, Checked = f.Enabled };
        _nodesBuilt++;
        foreach (var child in f.Children) node.Nodes.Add(BuildNode(child));
        if (f.Children.Count > 0 && !_collapsed.Contains(f.Id)) node.Expand();
        return node;
    }

    /// <summary>Brings the list back in line with the model without blanking it.
    ///
    /// Nodes are matched to filters by id and only what actually differs is touched, so an undo - whose
    /// snapshot keeps every id - reuses the rows already on screen and repaints none of the ones it did not
    /// change. <see cref="Rebuild"/> throws the lot away and then has to put the selection and the scroll
    /// position back afterwards, and those two restores each scroll the tree: that is the flash. Use this
    /// whenever the same filters are still there, and Rebuild only when they are genuinely a different set.</summary>
    public void SyncToModel()
    {
        if (_doc is null) return;
        _building = true;
        _tree.BeginUpdate();
        try { SyncLevel(_tree.Nodes, _doc.Filters.Roots); }
        finally { _tree.EndUpdate(); _building = false; }

        _flat.Clear();
        FlattenInto(_tree.Nodes);
        if (_collapsed.Count > 0) _collapsed.IntersectWith(_flat.Select(n => (n.Tag as Filter)?.Id ?? ""));
        MeasureDescriptions();
        MeasureCounts();
    }

    private void SyncLevel(TreeNodeCollection nodes, List<Filter> filters)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            var node = i < nodes.Count && IdOf(nodes[i]) == f.Id ? nodes[i] : NodeAt(nodes, f, i);
            // The filter object is a different instance after a restore even though it is the same filter,
            // so the tag is re-pointed every time; text and tick only when they read differently.
            node.Tag = f;
            string text = f.Match.ToDisplayString();
            if (node.Text != text) node.Text = text;
            if (node.Checked != f.Enabled) node.Checked = f.Enabled;
            SyncLevel(node.Nodes, f.Children);
            if (f.Children.Count > 0)
            {
                if (_collapsed.Contains(f.Id)) node.Collapse();
                else node.Expand();
            }
        }
        while (nodes.Count > filters.Count) nodes.RemoveAt(nodes.Count - 1);
    }

    /// <summary>Puts this filter's node at <paramref name="index"/>: moved up from further down the level if
    /// it is already somewhere in it, and built if it is not.</summary>
    private TreeNode NodeAt(TreeNodeCollection nodes, Filter f, int index)
    {
        for (int j = index + 1; j < nodes.Count; j++)
        {
            if (IdOf(nodes[j]) != f.Id) continue;
            var moved = nodes[j];
            nodes.RemoveAt(j);
            nodes.Insert(index, moved);
            return moved;
        }
        var made = new TreeNode(f.Match.ToDisplayString()) { Tag = f, Checked = f.Enabled };
        _nodesBuilt++;
        nodes.Insert(index, made);
        return made;
    }

    private static string? IdOf(TreeNode node) => (node.Tag as Filter)?.Id;

    /// <summary>Test seam: how many rows have ever been built. A sync that reuses the list leaves it alone,
    /// which is what "no flash" actually means.</summary>
    internal int NodesBuiltForTesting => _nodesBuilt;

    internal TreeNode? NodeForTesting(Filter f) => NodeFor(f);

    /// <summary>The filter at the top of the visible part of the list, which a rebuild loses and puts back
    /// - and putting it back is a scroll you can see.</summary>
    internal Filter? TopFilterForTesting => _tree.TopNode?.Tag as Filter;

    internal int PaintsForTesting => _tree.Paints;

    /// <summary>Rows on screen, flattened, and the order they are in.</summary>
    internal int RowCountForTesting => _flat.Count;

    internal string[] RowOrderForTesting => _flat.Select(n => n.Text).ToArray();

    internal void ScrollToForTesting(Filter f) { if (NodeFor(f) is { } n) _tree.TopNode = n; }

    private void FlattenInto(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes) { _flat.Add(n); FlattenInto(n.Nodes); }
    }

    private void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_building || e.Node?.Tag is not Filter f) return;
        // Shift takes everything below along with it. On its own the checkbox stays strictly per filter:
        // a disabled parent still scopes its children (its pattern is required of them either way), so
        // "off here, on underneath" is a real thing to want and cascading by default would destroy it.
        if ((ModifierKeys & Keys.Shift) == Keys.Shift) { SetSubtreeEnabled(e.Node, e.Node.Checked); return; }
        f.Enabled = e.Node.Checked;
        FiltersChanged?.Invoke();
    }

    // ---- type-to-search ----

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { JumpToMatch(fromSelection: true, forward: !e.Shift, announce: true); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Escape) { _search.Clear(); _tree.Focus(); e.Handled = e.SuppressKeyPress = true; }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F3) { JumpToMatch(fromSelection: true, forward: !e.Shift, announce: true); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Space && e.Shift && !e.Control && !e.Alt && _tree.SelectedNode is { } sel)
        {
            // Handled here rather than left to the tree's own Space, so the gesture does not depend on
            // what the native control makes of a modified Space.
            SetSubtreeEnabled(sel, !sel.Checked);
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.Control && !e.Shift && !e.Alt && e.KeyCode is Keys.Up or Keys.Down or Keys.Left or Keys.Right)
        {
            MoveSelected(e.KeyCode);
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.Handled = e.SuppressKeyPress = true;
            // Not opened here. The dialog runs its own message loop, which pumps the WM_CHAR belonging to
            // this very keystroke before WinForms gets to discard it - and the tree beeps at a character it
            // has no use for. SuppressKeyPress cannot help: the discard happens after this returns. Letting
            // the key finish first is what actually silences it. The guard is for a held Enter, which would
            // otherwise queue a second dialog on top of the first.
            if (!_editPending && SelectedFilter is { } f)
            {
                _editPending = true;
                BeginInvoke(() => { try { EditRequested?.Invoke(f); } finally { _editPending = false; } });
            }
        }
        else if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Escape && _search.TextLength > 0) { _search.Clear(); e.Handled = e.SuppressKeyPress = true; }
    }

    private void JumpToMatch(bool fromSelection, bool forward, bool announce)
    {
        string q = _search.Text.Trim();
        _tree.Invalidate();
        if (q.Length == 0 || _flat.Count == 0) return;

        int start = forward ? 0 : _flat.Count - 1;
        if (fromSelection && _tree.SelectedNode is not null)
        {
            int cur = _flat.IndexOf(_tree.SelectedNode);
            if (cur >= 0) start = cur + (forward ? 1 : -1);
        }

        // Stops at the ends instead of wrapping, so "no more matches" means the same thing here as it does
        // for the other find commands.
        for (int idx = start; idx >= 0 && idx < _flat.Count; idx += forward ? 1 : -1)
        {
            if (!Matches(_flat[idx], q)) continue;
            _tree.SelectedNode = _flat[idx];
            RevealNode(_flat[idx]);
            _tree.Invalidate();
            return;
        }

        // Typing filters incrementally, so only an explicit Enter/F3 reports the end - otherwise every
        // keystroke of a term that does not match yet would report failure.
        if (announce) NoFilterMatch?.Invoke(q);
    }

    private static bool Matches(TreeNode node, string query)
    {
        if (node.Tag is not Filter f) return false;
        return f.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || f.Match.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    // ---- columns: measured from what is actually in them ----

    private FilterColumns _columns;
    private int _descDesired;    // widest description; only changes with the list or the font
    private int _countDesired;   // widest count; grows as filtering streams results in

    private static readonly Size Unbounded = new(int.MaxValue, int.MaxValue);
    private const TextFormatFlags MeasureFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private int Inset => LogicalToDeviceUnits(4);

    private static int Measure(string text, Font font) => TextRenderer.MeasureText(text, font, Unbounded, MeasureFlags).Width;

    /// <summary>Test seam: where the columns ended up, in tree client coordinates.</summary>
    internal FilterColumns ColumnsForTesting => _columns;

    /// <summary>Test seam: the tree's own bounds inside this control, for pixel comparisons.</summary>
    internal Rectangle TreeAreaForTesting => _tree.Bounds;

    /// <summary>What the count column shows for a filter, or "" when it shows nothing.</summary>
    private string CountTextFor(Filter f)
    {
        if (!f.Enabled || _doc is null) return "";
        long count = _doc.MatchCountFor(f);
        bool busy = !_doc.IsFilterIdle;
        return count >= 0 ? (busy ? $"{count:N0}\u2026" : $"{count:N0}") : (busy ? "\u2026" : "");
    }

    private void MeasureDescriptions()
    {
        EnsureFonts();
        int widest = 0;
        foreach (var n in _flat)
            if (n.Tag is Filter f && !string.IsNullOrWhiteSpace(f.Description))
                widest = Math.Max(widest, Measure(f.Description, Pick(FontStyle.Bold)));
        _descDesired = widest == 0 ? 0 : Math.Max(widest, HeaderWidth("Description")) + Inset * 2;
        LayoutColumns();
    }

    private void MeasureCounts()
    {
        EnsureFonts();
        int widest = 0;
        foreach (var n in _flat)
            if (n.Tag is Filter f && CountTextFor(f) is { Length: > 0 } text)
                widest = Math.Max(widest, Measure(text, Pick(FontStyle.Bold)));
        int desired = widest == 0 ? 0 : Math.Max(widest, HeaderWidth("Count")) + Inset * 2;

        // Counts only climb during a pass, and a column that narrows and widens again on the way is far more
        // distracting than one that is briefly a few pixels too wide. Shrink only once the pass is over.
        _countDesired = (_doc?.IsFilterIdle ?? true) ? desired : Math.Max(_countDesired, desired);
        LayoutColumns();
    }

    /// <summary>A column is never narrower than its own title: a header reading "C…" looks broken however
    /// well the numbers underneath it fit. Only the half-the-space cap below can overrule it, and only
    /// because there is genuinely nowhere else for the width to come from.</summary>
    private int HeaderWidth(string title) => TextRenderer.MeasureText(title, _header.Font, Unbounded, MeasureFlags).Width;

    /// <summary>Test seam: how much a heading needs, so a test can assert a column left room for its own.</summary>
    internal int HeaderWidthForTesting(string title) => HeaderWidth(title);

    /// <summary>Count is whatever the longest count needs, never less than its own heading. Of what is left,
    /// the description takes what it needs up to half; the pattern gets the rest, so it always has at least
    /// half of the space the count did not want.</summary>
    private void LayoutColumns()
    {
        int available = _tree.ClientSize.Width;
        int count = Math.Clamp(_countDesired, 0, Math.Max(0, available));
        int room = Math.Max(0, available - count);
        int desc = _descDesired == 0 ? 0 : Math.Min(_descDesired, room / 2);

        var columns = new FilterColumns(available - count - desc, available - count, desc, count);
        if (columns == _columns) return;
        _columns = columns;
        _header.SetColumns(columns);
        QueueTooltipUpdate();
        _tree.Invalidate();
    }

    /// <summary>Tooltips are worked out from a posted message rather than in place, because measuring a row
    /// asks the tree for its bounds and the tree answers that by DRAWING - and drawing part way through a
    /// layout, or through its own window being created, fails outright in GDI+. Nothing needs them sooner
    /// than the next turn of the message loop.</summary>
    private void QueueTooltipUpdate()
    {
        if (_tooltipsQueued || !_tree.IsHandleCreated) return;
        _tooltipsQueued = true;
        _tree.BeginInvoke(() => { _tooltipsQueued = false; UpdateTooltips(); });
    }

    /// <summary>Only the rows that had to be cut short get a tooltip - there is no scrolling to reach the
    /// rest of the text, and a tooltip on everything would just be noise.</summary>
    private void UpdateTooltips()
    {
        if (!_tree.IsHandleCreated) return;
        EnsureFonts();
        foreach (var n in _flat)
        {
            if (n.Tag is not Filter f) { n.ToolTipText = ""; continue; }
            int patternRoom = _columns.FilterRight - (n.Bounds.Left + 2) - Inset;
            bool patternCut = Measure(n.Text, Pick(FontStyle.Bold)) > patternRoom;
            bool descCut = !string.IsNullOrWhiteSpace(f.Description)
                           && Measure(f.Description, Pick(FontStyle.Bold)) > _columns.DescriptionWidth - Inset * 2;
            n.ToolTipText = patternCut || descCut
                ? (string.IsNullOrWhiteSpace(f.Description) ? n.Text : $"{n.Text}\n{f.Description}")
                : "";
        }
    }

    // ---- owner draw (color swatch, exclude style, bold search matches, drop indicator) ----

    private void OnDrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node?.Tag is not Filter f) { e.DrawDefault = true; return; }
        EnsureFonts();
        var g = e.Graphics;
        Rectangle bounds = e.Bounds;
        bool selected = (e.State & TreeNodeStates.Selected) != 0;
        int h = bounds.Height;
        int contentLeft = ContentLeft(e.Node);

        // Resolve the filter's effective style so the row previews exactly how a matching line looks.
        var defaults = new ResolvedStyle(ToRgb(_settings.Foreground), ToRgb(_settings.Background), false, false);
        var rs = StyleResolver.Resolve(f, defaults);
        Color bg = ToColor(rs.Background);
        Color fg = ToColor(rs.Foreground);
        FontStyle style = (rs.Bold ? FontStyle.Bold : 0) | (rs.Italic ? FontStyle.Italic : 0);

        // While a filter search is active, matching filters keep their colors (with the matched term bold,
        // drawn below); non-matching filters are shown colorless and dimmed so the matches stand out.
        string sq = _search.Text.Trim();
        if (sq.Length > 0 && !Matches(e.Node, sq))
        {
            bg = _settings.Background;
            fg = _settings.DimForeground;
            style = FontStyle.Regular;
        }

        if (_dragNode is not null && IsSelfOrDescendantOfDrag(e.Node))
        {
            fg = Fade(fg, bg);
            selected = false;   // the fade is the state to read, not a selection frame
        }

        int rightEdge = _tree.ClientSize.Width;
        int countX = _columns.CountX;
        int descX = _columns.DescX;
        int filterRight = _columns.FilterRight;

        using (var b = new SolidBrush(bg))
        {
            // Fill from the label's own left edge, not from where the text starts. Windows has already
            // painted its selection across the whole label, so any part left uncovered shows through as a
            // stripe between the checkbox and the text.
            int fill = e.Node.Bounds.Left;
            g.FillRectangle(b, fill, bounds.Top, Math.Max(0, rightEdge - fill), h);
        }

        int textHeight = TextRenderer.MeasureText(g, "Xg", _fReg, new Size(int.MaxValue, h), TextFormatFlags.NoPadding).Height;
        int textY = bounds.Top + Math.Max(0, (h - textHeight) / 2);
        string pattern = (f.Kind == FilterKind.Exclude ? "\u2260 " : "") + e.Node.Text;

        var savedClip = g.Clip;
        g.SetClip(Rectangle.FromLTRB(contentLeft, bounds.Top, Math.Max(contentLeft, filterRight - Inset), bounds.Bottom));
        DrawWithSearchHighlight(g, pattern, new Point(contentLeft, textY),
                                filterRight - Inset - contentLeft, fg, style);

        if (_columns.HasDescription && !string.IsNullOrEmpty(f.Description))
        {
            g.SetClip(Rectangle.FromLTRB(descX + Inset, bounds.Top, Math.Max(descX + Inset, countX - 2), bounds.Bottom));
            TextRenderer.DrawText(g, f.Description, Pick(style),
                new Rectangle(descX + Inset, textY, Math.Max(0, _columns.DescriptionWidth - Inset * 2), textHeight), fg,
                TextFlags | TextFormatFlags.EndEllipsis);
        }

        if (_columns.HasCount && CountTextFor(f) is { Length: > 0 } countText)
        {
            g.SetClip(Rectangle.FromLTRB(countX + 2, bounds.Top, rightEdge, bounds.Bottom));
            TextRenderer.DrawText(g, countText, Pick(style),
                new Rectangle(countX + Inset, textY, Math.Max(0, _columns.CountWidth - Inset * 2), textHeight), fg,
                TextFlags | TextFormatFlags.Right);
        }
        g.Clip = savedClip;

        using (var pen = new Pen(Color.FromArgb(40, Color.Gray)))
        {
            if (_columns.HasDescription) g.DrawLine(pen, descX, bounds.Top, descX, bounds.Bottom);
            if (_columns.HasCount) g.DrawLine(pen, countX, bounds.Top, countX, bounds.Bottom);
        }

        if (selected)
        {
            using var selPen = new Pen(SystemColors.Highlight);
            g.DrawRectangle(selPen, 0, bounds.Top, Math.Max(1, rightEdge - 1), h - 1);
        }
    }

    private static RgbColor ToRgb(Color c) => new(c.R, c.G, c.B);
    private static Color ToColor(RgbColor c) => Color.FromArgb(c.R, c.G, c.B);

    private void EnsureFonts()
    {
        if (_fBase is not null && _fBase.Equals(_tree.Font)) return;
        _fReg?.Dispose(); _fBold?.Dispose(); _fItalic?.Dispose(); _fBoldItalic?.Dispose();
        _fBase = _tree.Font;
        _fReg = new Font(_fBase, FontStyle.Regular);
        _fBold = new Font(_fBase, FontStyle.Bold);
        _fItalic = new Font(_fBase, FontStyle.Italic);
        _fBoldItalic = new Font(_fBase, FontStyle.Bold | FontStyle.Italic);
    }

    private Font Pick(FontStyle style) =>
        style.HasFlag(FontStyle.Bold) && style.HasFlag(FontStyle.Italic) ? _fBoldItalic :
        style.HasFlag(FontStyle.Bold) ? _fBold :
        style.HasFlag(FontStyle.Italic) ? _fItalic : _fReg;

    /// <see cref="TextFormatFlags.PreserveGraphicsClipping"/> is the load-bearing one: TextRenderer draws
    /// through GDI, which knows nothing about the GDI+ clip these columns are set up with, so without it
    /// every column's text paints straight across the ones beside it.
    private const TextFormatFlags TextFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;

    private int DrawWithSearchHighlight(Graphics g, string text, Point pt, int maxWidth, Color color, FontStyle style)
    {
        text = Ellipsize(text, Pick(style), maxWidth);
        string q = _search.Text.Trim();
        int x = pt.X;
        void Draw(string s, Font f)
        {
            if (s.Length == 0) return;
            TextRenderer.DrawText(g, s, f, new Point(x, pt.Y), color, TextFlags);
            x += TextRenderer.MeasureText(g, s, f, new Size(int.MaxValue, 200), MeasureFlags).Width;
        }

        int matchStart = q.Length == 0 ? -1 : text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (matchStart < 0) { Draw(text, Pick(style)); return x; }

        Draw(text[..matchStart], Pick(style));
        Draw(text.Substring(matchStart, q.Length), Pick(style | FontStyle.Bold));
        Draw(text[(matchStart + q.Length)..], Pick(style));
        return x;
    }

    /// <summary>Trims text to fit, ending in an ellipsis. Binary search rather than a walk from the end -
    /// this runs for every visible row on every repaint.</summary>
    private static string Ellipsize(string text, Font font, int maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (Measure(text, font) <= maxWidth) return text;

        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Measure(text[..mid] + "\u2026", font) <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return lo == 0 ? "\u2026" : text[..lo] + "\u2026";
    }

    /// <summary>The dragged filter and everything under it are drawn faded while the drag is in progress:
    /// the list already shows them where they would land, and the fade is what says "not yet committed".</summary>
    private static Color Fade(Color c, Color towards) =>
        Color.FromArgb((c.R + towards.R * 2) / 3, (c.G + towards.G * 2) / 3, (c.B + towards.B * 2) / 3);

    // ---- drag & drop reorder + nest ----

    private void OnTreeMouseMove(object? sender, MouseEventArgs e)
    {
        if (_pressed is null || e.Button != MouseButtons.Left || _dragNode is not null) return;

        // The system's own threshold, so a press that wobbles still reads as a click.
        var slop = SystemInformation.DragSize;
        if (Math.Abs(e.X - _pressedAt.X) < slop.Width && Math.Abs(e.Y - _pressedAt.Y) < slop.Height) return;

        var node = _pressed;
        _pressed = null;
        if (_doc is null || node.Tag is not Filter f) return;

        BeginDrag(node, f, _pressedAt.X);
        try { DoDragDrop(node, DragDropEffects.Move); }
        finally { StopAutoScroll(); }
    }

    private void BeginDrag(TreeNode n, Filter f, int grabX)
    {
        BeforeFiltersEdited?.Invoke("Move Filter");
        _dragNode = n;
        _dragOrigin = (f.Parent, (f.Parent?.Children ?? _doc!.Filters.Roots).IndexOf(f));
        _dragGrabX = grabX;
        _dragGrabLevel = n.Level;
        _tree.SelectedNode = n;
        SetDragSubtreeCollapsed(true);
        _tree.Invalidate();
    }

    /// <summary>Test seams: a real drag is a modal DoDragDrop loop, which a test cannot run - these drive
    /// the same code from the outside. Everything after the grab is shared with the real thing.</summary>
    internal void StartDragForTesting(Filter f, Point at)
    {
        if (_doc is not null && NodeFor(f) is { } n) BeginDrag(n, f, at.X);
    }

    internal void DragToForTesting(Point at)
    {
        AutoScrollFor(at);
        UpdateDropPosition(at);
    }

    internal void DropForTesting() => ResetDrag();

    /// <summary>One beat of the auto-scroll timer, which a test cannot wait on reliably.</summary>
    internal void AutoScrollTickForTesting() => AutoScrollTick();

    internal int RowHeightForTesting => _tree.ItemHeight;
    internal int TreeHeightForTesting => _tree.ClientSize.Height;
    internal int TreeWidthForTesting => _tree.ClientSize.Width;
    internal bool IsExpandedForTesting(Filter f) => NodeFor(f)?.IsExpanded ?? false;
    internal Rectangle RowBoundsForTesting(Filter f) => NodeFor(f)?.Bounds ?? Rectangle.Empty;
    internal bool IsCheckedForTesting(Filter f) => NodeFor(f)?.Checked ?? false;
    internal void SelectForTesting(Filter f) { if (NodeFor(f) is { } n) _tree.SelectedNode = n; }
    internal void PressKeyForTesting(Keys key) => OnTreeKeyDown(_tree, new KeyEventArgs(key));
    internal void ToggleCheckboxForTesting(Filter f) { if (NodeFor(f) is { } n) n.Checked = !n.Checked; }

    /// <summary>Whether pressing here would pick the filter up, by way of the real handler.</summary>
    internal bool PressArmsDragForTesting(Point at)
    {
        OnTreeMouseDown(_tree, new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0));
        bool armed = _pressed is not null;
        _pressed = null;
        return armed;
    }

    /// <summary>Double-clicks here through the real handler, and reports the filter it asked to edit.</summary>
    internal Filter? DoubleClickForTesting(Point at)
    {
        Filter? asked = null;
        void Watch(Filter f) => asked = f;
        EditRequested += Watch;
        try { if (_tree.GetNodeAt(at) is { } node) HandleDoubleClick(node, at.X); }
        finally { EditRequested -= Watch; }
        return asked;
    }

    /// <summary>Sends the whole message sequence a real double-click produces - down, up, double-click, up -
    /// because it is the tree's handling of that last pair which used to leave a checkbox and its filter
    /// disagreeing. A lone double-click message is ignored and would prove nothing.</summary>
    internal void SendDoubleClickForTesting(Point at)
    {
        var lParam = (IntPtr)((at.Y << 16) | (at.X & 0xFFFF));
        SendMessage(_tree.Handle, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
        SendMessage(_tree.Handle, WM_LBUTTONUP, IntPtr.Zero, lParam);
        SendMessage(_tree.Handle, WM_LBUTTONDBLCLK, (IntPtr)MK_LBUTTON, lParam);
        SendMessage(_tree.Handle, WM_LBUTTONUP, IntPtr.Zero, lParam);
    }

    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, MK_LBUTTON = 0x0001;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>Test seam: the filters on screen, top first.</summary>
    internal List<Filter> VisibleFiltersForTesting
    {
        get
        {
            var list = new List<Filter>();
            for (var n = _tree.TopNode; n is not null && n.Bounds.Top < _tree.ClientSize.Height; n = n.NextVisibleNode)
                if (n.Tag is Filter f) list.Add(f);
            return list;
        }
    }

    /// <summary>A subtree is carried collapsed, the way outliners do it. At full height it fills the pane
    /// it is being dragged through, leaving almost no rows to aim between - so the filter sits still for
    /// most of the pane and then leaps several places at once. This is presentation only: <c>_building</c>
    /// keeps it out of the user's own collapsed set, and dropping puts back exactly what they had open.</summary>
    private void SetDragSubtreeCollapsed(bool collapsed)
    {
        if (_dragNode is null || _dragNode.Nodes.Count == 0) return;
        _building = true;
        _tree.BeginUpdate();
        try { if (collapsed) _dragNode.Collapse(); else RestoreExpansion(_dragNode); }
        finally { _tree.EndUpdate(); _building = false; }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_doc is null || _dragNode is null) { e.Effect = DragDropEffects.None; return; }
        e.Effect = DragDropEffects.Move;

        var pt = _tree.PointToClient(new Point(e.X, e.Y));
        AutoScrollFor(pt);
        UpdateDropPosition(pt);
    }

    private void UpdateDropPosition(Point pt)
    {
        if (_doc is null || _dragNode?.Tag is not Filter dragged) return;
        _dragPoint = pt;

        var rows = new List<TreeNode>();
        for (var n = _tree.Nodes.Count > 0 ? _tree.Nodes[0] : null; n is not null; n = n.NextVisibleNode)
            if (!IsSelfOrDescendantOfDrag(n)) rows.Add(n);

        // Nesting follows how far right the pointer has travelled since the grab, not where it happens to
        // be: dragging straight down must not re-nest an item just because it was picked up by its middle.
        var spot = DropPlacement.For(
            rows.Select(n => new DropRow(n.Level, n.Bounds.Top, n.Bounds.Height)).ToList(),
            pt.Y, _dragGrabLevel * _tree.Indent + (pt.X - _dragGrabX), _tree.Indent);

        // Turn the slot into a parent and a position among that parent's children.
        Filter? parent = null;
        if (spot.Slot > 0)
        {
            var above = (Filter)rows[spot.Slot - 1].Tag!;
            for (int level = rows[spot.Slot - 1].Level; level >= spot.Level; level--) above = above.Parent!;
            parent = spot.Level == 0 ? null : above;
        }
        var siblings = parent?.Children ?? _doc.Filters.Roots;
        int index = spot.Slot == 0 ? 0 : IndexAfter(siblings, (Filter)rows[spot.Slot - 1].Tag!, spot.Level, rows[spot.Slot - 1].Level);

        MoveLive(dragged, parent, index);
    }

    /// <summary>The slot in <paramref name="siblings"/> just after the row above the drop, once that row has
    /// been walked up to the level being dropped at.</summary>
    private static int IndexAfter(List<Filter> siblings, Filter above, int dropLevel, int aboveLevel)
    {
        var anchor = above;
        for (int level = aboveLevel; level > dropLevel; level--) anchor = anchor.Parent!;
        int i = siblings.IndexOf(anchor);
        return i < 0 ? siblings.Count : i + 1;
    }

    private bool IsSelfOrDescendantOfDrag(TreeNode n)
    {
        for (var p = n; p is not null; p = p.Parent)
            if (ReferenceEquals(p, _dragNode)) return true;
        return false;
    }

    /// <summary>Puts the filter where it would land if dropped now - in the model and in the tree - so the
    /// list always shows the outcome rather than a hint of it. The tree node is moved rather than rebuilt:
    /// rebuilding blanks and repopulates the whole list, which flickers on every mouse move.</summary>
    private void MoveLive(Filter dragged, Filter? parent, int index)
    {
        if (_doc is null || _dragNode is null) return;

        var currentList = dragged.Parent?.Children ?? _doc.Filters.Roots;
        var targetList = parent?.Children ?? _doc.Filters.Roots;
        int currentIndex = currentList.IndexOf(dragged);
        if (ReferenceEquals(currentList, targetList) && (index == currentIndex || index == currentIndex + 1)) return;
        if (!_doc.Filters.Move(dragged, parent, index)) return;

        _building = true;
        _tree.BeginUpdate();
        try
        {
            _dragNode.Remove();
            var nodes = parent is null ? _tree.Nodes : NodeFor(parent)?.Nodes;
            if (nodes is null) { _doc.Filters.Move(dragged, currentList == _doc.Filters.Roots ? null : dragged.Parent, currentIndex); return; }

            int at = (parent?.Children ?? _doc.Filters.Roots).IndexOf(dragged);
            nodes.Insert(Math.Clamp(at, 0, nodes.Count), _dragNode);
            NodeFor(parent)?.Expand();
        }
        finally
        {
            _tree.EndUpdate();
            _building = false;
        }

        _flat.Clear();
        FlattenInto(_tree.Nodes);
        _tree.SelectedNode = _dragNode;
        _tree.Invalidate();
    }

    private TreeNode? NodeFor(Filter? f)
        => f is null ? null : _flat.FirstOrDefault(n => ReferenceEquals(n.Tag, f)) ?? FindNode(_tree.Nodes, f);

    private static TreeNode? FindNode(TreeNodeCollection nodes, Filter f)
    {
        foreach (TreeNode n in nodes)
        {
            if (ReferenceEquals(n.Tag, f)) return n;
            if (FindNode(n.Nodes, f) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>Re-inserting a node collapses it, so put back what the user had open.</summary>
    private void RestoreExpansion(TreeNode node)
    {
        if (node.Tag is Filter f && node.Nodes.Count > 0 && !_collapsed.Contains(f.Id)) node.Expand();
        foreach (TreeNode child in node.Nodes) RestoreExpansion(child);
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        StopAutoScroll();
        bool moved = _dragNode is not null && _doc is not null && _dragOrigin is { } origin
                     && _dragNode.Tag is Filter f
                     && (!ReferenceEquals(f.Parent, origin.Parent)
                         || (f.Parent?.Children ?? _doc.Filters.Roots).IndexOf(f) != origin.Index);
        ResetDrag();
        if (moved) FiltersChanged?.Invoke();   // the only re-evaluation: none of the live moves triggered one
    }

    /// <summary>Escape during a drag, or dropping nowhere, puts the filter back where it started.</summary>
    private void CancelDrag()
    {
        StopAutoScroll();
        if (_doc is not null && _dragNode?.Tag is Filter f && _dragOrigin is { } origin)
            MoveLive(f, origin.Parent, origin.Index);
        ResetDrag();
    }

    private void ResetDrag()
    {
        SetDragSubtreeCollapsed(false);
        _dragNode = null;
        _dragOrigin = null;
        StopAutoScroll();
        _tree.Invalidate();
    }

    // ---- auto-scroll while dragging ----

    /// <summary>Dragging into the top or bottom edge scrolls the list, so a filter can be taken somewhere
    /// that is not on screen when the drag starts. The nearer the edge, the faster it goes.</summary>
    private void AutoScrollFor(Point pt)
    {
        int zone = Math.Max(_tree.ItemHeight, LogicalToDeviceUnits(24));
        int step = pt.Y < zone ? -1 : pt.Y > _tree.ClientSize.Height - zone ? 1 : 0;
        if (step != 0)
        {
            int depth = step < 0 ? zone - pt.Y : pt.Y - (_tree.ClientSize.Height - zone);
            step *= depth > zone / 2 ? 3 : 1;
        }

        _autoScrollStep = step;
        if (step == 0) _autoScroll.Stop();
        else if (!_autoScroll.Enabled) { ScrollBy(step); _autoScroll.Start(); }
    }

    private void StopAutoScroll()
    {
        _autoScroll.Stop();
        _autoScrollStep = 0;
    }

    private void AutoScrollTick()
    {
        ScrollBy(_autoScrollStep);
        if (_dragNode is not null) UpdateDropPosition(_dragPoint);
    }

    private void ScrollBy(int rows)
    {
        if (_tree.TopNode is not { } top) return;
        for (int i = 0; i < Math.Abs(rows); i++)
        {
            var next = rows < 0 ? top.PrevVisibleNode : top.NextVisibleNode;
            if (next is null) break;
            top = next;
        }
        if (!ReferenceEquals(top, _tree.TopNode)) _tree.TopNode = top;
    }

    /// <summary>Brings a searched-for filter into the middle half of the list, the same way the log view
    /// reveals a line it jumps to - a match pinned against the top or bottom edge hides the siblings that
    /// give it its meaning. A match already inside that band does not move.</summary>
    private void RevealNode(TreeNode node)
    {
        node.EnsureVisible();   // opens any collapsed ancestors, so the node is on a visible row from here on
        int visible = Math.Max(1, _tree.ClientSize.Height / Math.Max(1, _tree.ItemHeight));
        int top = visible / 4;
        int bottom = Math.Max(top, visible * 3 / 4 - 1);

        int offset = -1;
        int i = 0;
        for (var n = _tree.TopNode; n is not null && i <= visible; n = n.NextVisibleNode, i++)
            if (ReferenceEquals(n, node)) { offset = i; break; }
        if (offset < 0) return;

        if (offset < top) ScrollBy(offset - top);
        else if (offset > bottom) ScrollBy(offset - bottom);
    }

    // ---- context menu / edits ----

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Add Filter…", null, (_, _) => AddRequested?.Invoke(null));
        menu.Items.Add("Add Child Filter…", null, (_, _) => AddRequested?.Invoke(SelectedFilter));
        menu.Items.Add("Edit Filter…", null, (_, _) => { if (SelectedFilter is { } f) EditRequested?.Invoke(f); });
        menu.Items.Add(new ToolStripMenuItem("Duplicate Filter", null, (_, _) => DuplicateSelected()) { ShortcutKeyDisplayString = "Ctrl+D" });
        menu.Items.Add("Remove Filter", null, (_, _) => RemoveSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Find Next Match", null, (_, _) => { if (SelectedFilter is { } f) FindFilterRequested?.Invoke(f, true); }) { ShortcutKeyDisplayString = "F4" });
        menu.Items.Add(new ToolStripMenuItem("Find Previous Match", null, (_, _) => { if (SelectedFilter is { } f) FindFilterRequested?.Invoke(f, false); }) { ShortcutKeyDisplayString = "Shift+F4" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Enable Subtree", null, (_, _) => SetSelectedSubtreeEnabled(true)) { ShortcutKeyDisplayString = "Shift+Space" });
        menu.Items.Add(new ToolStripMenuItem("Disable Subtree", null, (_, _) => SetSelectedSubtreeEnabled(false)) { ShortcutKeyDisplayString = "Shift+Space" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Enable All", null, (_, _) => SetAllEnabled(true));
        menu.Items.Add("Disable All", null, (_, _) => SetAllEnabled(false));
        menu.Items.Add("Remove All", null, (_, _) => RemoveAll());
        return menu;
    }

    public void RemoveSelected()
    {
        if (_doc is null || SelectedFilter is not { } f) return;
        BeforeFiltersEdited?.Invoke("Remove Filter");
        var node = _flat.FirstOrDefault(n => ReferenceEquals(n.Tag, f));
        _doc.Filters.Remove(f);

        if (node is null) Rebuild();   // not on screen for some reason; fall back to a full refresh
        else
        {
            // Drop just this node (its children go with it) instead of calling Rebuild(). Rebuild clears
            // and recreates every node, so the whole list blanks and repopulates - a visible flash on every
            // delete. Removing one node leaves the rest of the tree, its scroll position and its expansion
            // state completely untouched.
            var next = node.NextNode ?? node.PrevNode ?? node.Parent;
            _building = true;
            _tree.BeginUpdate();
            node.Remove();
            _flat.Clear();
            FlattenInto(_tree.Nodes);
            _tree.EndUpdate();
            _building = false;
            // Keep a sensible selection so repeated Delete presses keep working without re-clicking.
            if (next is not null) _tree.SelectedNode = next;
        }

        FiltersChanged?.Invoke();
    }

    /// <summary>Ctrl+Up/Down reorders the selected filter among its siblings; Ctrl+Right nests it under the
    /// filter above it, Ctrl+Left moves it back out one level.</summary>
    public void MoveSelected(Keys key)
    {
        if (_doc is null || SelectedFilter is not { } f) return;
        BeforeFiltersEdited?.Invoke("Move Filter");
        bool moved = key switch
        {
            Keys.Up => _doc.Filters.Reorder(f, -1),
            Keys.Down => _doc.Filters.Reorder(f, +1),
            Keys.Right => _doc.Filters.Indent(f),
            Keys.Left => _doc.Filters.Outdent(f),
            _ => false
        };
        if (!moved) return;   // already at an end / top level: leave the tree alone
        SyncMovedNode(f);
        // Nesting changes what a filter matches, and sibling order decides which filter colours a line, so
        // the snapshot has to be rebuilt either way.
        FiltersChanged?.Invoke();
    }

    /// <summary>Re-homes just the moved filter's node so the tree matches the model again. Rebuild() would
    /// blank and repopulate the whole list - very visible when holding Ctrl+Up.</summary>
    private void SyncMovedNode(Filter f)
    {
        var node = _flat.FirstOrDefault(n => ReferenceEquals(n.Tag, f));
        if (node is null || _doc is null) { Rebuild(); return; }

        var parentNode = f.Parent is null ? null : _flat.FirstOrDefault(n => ReferenceEquals(n.Tag, f.Parent));
        bool wasExpanded = node.IsExpanded;

        _building = true;
        _tree.BeginUpdate();
        node.Remove();
        var siblings = parentNode?.Nodes ?? _tree.Nodes;
        int index = (f.Parent?.Children ?? _doc.Filters.Roots).IndexOf(f);
        if (index < 0 || index > siblings.Count) siblings.Add(node);
        else siblings.Insert(index, node);
        if (wasExpanded) node.Expand();
        // A filter nested under a collapsed parent would simply vanish, so open it (and remember that it is
        // open, since _building suppresses the AfterExpand handler that normally tracks this).
        if (parentNode?.Tag is Filter parent) { _collapsed.Remove(parent.Id); parentNode.Expand(); }
        _flat.Clear();
        FlattenInto(_tree.Nodes);
        _tree.EndUpdate();
        _building = false;

        _tree.SelectedNode = node;
        node.EnsureVisible();
    }

    /// <summary>Copies the selected filter and everything under it in as the next sibling. The copy gets
    /// fresh ids: it is a new filter that happens to look like an existing one, and sharing ids would make
    /// the two indistinguishable to the list, the presets and the match cache.</summary>
    public void DuplicateSelected()
    {
        if (_doc is null || SelectedFilter is not { } f) return;
        BeforeFiltersEdited?.Invoke("Duplicate Filter");
        var copy = f.Clone();
        var siblings = f.Parent?.Children ?? _doc.Filters.Roots;
        _doc.Filters.Add(copy, f.Parent, siblings.IndexOf(f) + 1);
        SyncToModel();
        if (NodeFor(copy) is { } node) { _tree.SelectedNode = node; node.EnsureVisible(); }
        FiltersChanged?.Invoke();
    }

    /// <summary>Brings the checkboxes back in line with the model, for when something other than the list
    /// itself decided which filters are enabled - applying a preset. In place rather than a rebuild: the
    /// list must not blank, and the scroll position and selection have no reason to move.</summary>
    public void RefreshCheckStates()
    {
        if (_doc is null) return;
        _building = true;
        _tree.BeginUpdate();
        foreach (var n in _flat)
            if (n.Tag is Filter f && n.Checked != f.Enabled) n.Checked = f.Enabled;
        _tree.EndUpdate();
        _building = false;
        _tree.Invalidate();
    }

    public void SetAllEnabled(bool enabled)
    {
        if (_doc is null) return;
        // Toggle the check state in place instead of rebuilding the tree. Rebuild() clears and recreates
        // every node (the list blanks then repopulates → a visible flash); enable/disable-all only changes
        // each node's checkbox. _building suppresses the per-node AfterCheck → FiltersChanged; BeginUpdate
        // batches the repaint so it's flicker-free.
        _building = true;
        _tree.BeginUpdate();
        foreach (var n in _flat)
        {
            if (n.Tag is Filter f) f.Enabled = enabled;
            n.Checked = enabled;
        }
        _tree.EndUpdate();
        _building = false;
        _tree.Invalidate();
        FiltersChanged?.Invoke();
    }

    /// <summary>Sets the selected filter and everything nested under it to one state - the Shift+Space and
    /// Shift+click gesture, and the menu entries for it.</summary>
    public void SetSelectedSubtreeEnabled(bool enabled)
    {
        if (_tree.SelectedNode is { } n) SetSubtreeEnabled(n, enabled);
    }

    private void SetSubtreeEnabled(TreeNode root, bool enabled)
    {
        if (_doc is null) return;
        // Same in-place rule as enable/disable-all: only the checkboxes change, so mutate them rather than
        // rebuilding, and let _building swallow the per-node AfterCheck so this raises one change, not many.
        _building = true;
        _tree.BeginUpdate();
        try { ApplyEnabled(root, enabled); }
        finally { _tree.EndUpdate(); _building = false; }
        _tree.Invalidate();
        FiltersChanged?.Invoke();
    }

    private static void ApplyEnabled(TreeNode node, bool enabled)
    {
        if (node.Tag is Filter f) f.Enabled = enabled;
        node.Checked = enabled;
        foreach (TreeNode child in node.Nodes) ApplyEnabled(child, enabled);
    }

    public void RemoveAll()
    {
        if (_doc is null) return;
        BeforeFiltersEdited?.Invoke("Remove All Filters");
        _doc.Filters.Roots.Clear();
        Rebuild();
        FiltersChanged?.Invoke();
    }

    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }

    /// <summary>Moves keyboard focus into the filter list (selecting the first filter if none is selected).</summary>
    public void FocusList()
    {
        if (_tree.SelectedNode is null && _flat.Count > 0) _tree.SelectedNode = _flat[0];
        _tree.Focus();
    }

    /// <summary>Selects the first filter (used by the screenshot/demo harness).</summary>
    public void SelectFirst() { if (_flat.Count > 0) _tree.SelectedNode = _flat[0]; }

    /// <summary>Sets the filter-search text (used by the screenshot/demo harness).</summary>
    internal void SetSearchText(string text) => _search.Text = text;

    /// <summary>True when the filter search box currently has keyboard focus.</summary>
    public bool SearchHasFocus => _search.Focused;

    /// <summary>True when the filter list (tree) currently has keyboard focus.</summary>
    public bool ListHasFocus => _tree.Focused;

    /// <summary>Repaints nodes so live match counts refresh while filtering streams.</summary>
    public void RefreshCounts()
    {
        MeasureCounts();
        _tree.Invalidate();
    }
}

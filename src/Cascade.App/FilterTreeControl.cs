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
    private enum DropMode { None, Before, After, Onto }

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
    private readonly List<TreeNode> _flat = new();
    private readonly HashSet<string> _collapsed = new(); // filter ids the user has collapsed

    private TreeNode? _dragNode;
    private TreeNode? _dropTarget;
    private DropMode _dropMode;

    public event Action? FiltersChanged;
    public event Action<Filter>? EditRequested;
    public event Action<Filter?>? AddRequested;
    public event Action<Filter, bool>? FindFilterRequested; // (filter, forward)

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

        _tree.AfterCheck += OnAfterCheck;
        _tree.AfterExpand += (_, e) => { if (!_building && e.Node?.Tag is Filter f) _collapsed.Remove(f.Id); };
        _tree.AfterCollapse += (_, e) => { if (!_building && e.Node?.Tag is Filter f) _collapsed.Add(f.Id); };
        _tree.NodeMouseDoubleClick += (_, e) => { if (e.Node?.Tag is Filter f) EditRequested?.Invoke(f); };
        _tree.MouseDown += OnTreeMouseDown;
        _tree.KeyDown += OnTreeKeyDown;
        _tree.DrawNode += OnDrawNode;
        _tree.ItemDrag += OnItemDrag;
        _tree.DragEnter += (_, e) => e.Effect = DragDropEffects.Move;
        _tree.DragOver += OnDragOver;
        _tree.DragDrop += OnDragDrop;

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
        foreach (var child in f.Children) node.Nodes.Add(BuildNode(child));
        if (f.Children.Count > 0 && !_collapsed.Contains(f.Id)) node.Expand();
        return node;
    }

    private void FlattenInto(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes) { _flat.Add(n); FlattenInto(n.Nodes); }
    }

    private void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_building || e.Node?.Tag is not Filter f) return;
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
        else if (e.Control && !e.Shift && !e.Alt && e.KeyCode is Keys.Up or Keys.Down or Keys.Left or Keys.Right)
        {
            MoveSelected(e.KeyCode);
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter) { if (SelectedFilter is { } f) EditRequested?.Invoke(f); e.Handled = e.SuppressKeyPress = true; }
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
            _flat[idx].EnsureVisible();
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

    private int Measure(string text, Font font) => TextRenderer.MeasureText(text, font, Unbounded, MeasureFlags).Width;

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
        UpdateTooltips();
        _tree.Invalidate();
    }

    /// <summary>Only the rows that had to be cut short get a tooltip - there is no scrolling to reach the
    /// rest of the text, and a tooltip on everything would just be noise.</summary>
    private void UpdateTooltips()
    {
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
        int contentLeft = e.Node.Bounds.Left + 2; // start just right of the checkbox (never overlap it)

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

        int rightEdge = _tree.ClientSize.Width;
        int countX = _columns.CountX;
        int descX = _columns.DescX;
        int filterRight = _columns.FilterRight;

        using (var b = new SolidBrush(bg)) g.FillRectangle(b, contentLeft, bounds.Top, Math.Max(0, rightEdge - contentLeft), h);

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

        DrawDropIndicator(g, e.Node, bounds);
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
    private string Ellipsize(string text, Font font, int maxWidth)
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

    private void DrawDropIndicator(Graphics g, TreeNode node, Rectangle bounds)
    {
        if (_dropTarget != node || _dropMode == DropMode.None) return;
        using var pen = new Pen(Color.FromArgb(0, 120, 215), 2);
        switch (_dropMode)
        {
            case DropMode.Before: g.DrawLine(pen, bounds.Left, bounds.Top, bounds.Right, bounds.Top); break;
            case DropMode.After: g.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1); break;
            case DropMode.Onto: g.DrawRectangle(pen, bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1); break;
        }
    }

    // ---- drag & drop reorder + nest ----

    private void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item is TreeNode n) { _dragNode = n; DoDragDrop(n, DragDropEffects.Move); }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var pt = _tree.PointToClient(new Point(e.X, e.Y));
        var target = _tree.GetNodeAt(pt);
        _dropTarget = target;
        _dropMode = DropMode.None;

        if (_dragNode is null || target is null || ReferenceEquals(target, _dragNode))
        {
            e.Effect = DragDropEffects.None;
            _tree.Invalidate();
            return;
        }

        int rel = pt.Y - target.Bounds.Top;
        int third = Math.Max(4, target.Bounds.Height / 3);
        _dropMode = rel < third ? DropMode.Before : rel > target.Bounds.Height - third ? DropMode.After : DropMode.Onto;

        var dragged = (Filter)_dragNode.Tag!;
        var targetFilter = (Filter)target.Tag!;
        Filter? newParent = _dropMode == DropMode.Onto ? targetFilter : targetFilter.Parent;
        e.Effect = _doc is not null && _doc.Filters.CanMove(dragged, newParent) ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effect == DragDropEffects.None) _dropMode = DropMode.None;
        _tree.Invalidate();
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (_doc is null || _dragNode?.Tag is not Filter dragged || _dropTarget?.Tag is not Filter target || _dropMode == DropMode.None)
        {
            ResetDrag();
            return;
        }

        Filter? newParent;
        int index;
        if (_dropMode == DropMode.Onto)
        {
            newParent = target;
            index = target.Children.Count;
        }
        else
        {
            newParent = target.Parent;
            List<Filter> siblings = newParent?.Children ?? _doc.Filters.Roots;
            index = siblings.IndexOf(target) + (_dropMode == DropMode.After ? 1 : 0);
        }

        if (_doc.Filters.Move(dragged, newParent, index))
        {
            ResetDrag();
            Rebuild();
            FiltersChanged?.Invoke();
        }
        else ResetDrag();
    }

    private void ResetDrag()
    {
        _dragNode = null;
        _dropTarget = null;
        _dropMode = DropMode.None;
        _tree.Invalidate();
    }

    // ---- context menu / edits ----

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Add filter…", null, (_, _) => AddRequested?.Invoke(null));
        menu.Items.Add("Add child filter…", null, (_, _) => AddRequested?.Invoke(SelectedFilter));
        menu.Items.Add("Edit…", null, (_, _) => { if (SelectedFilter is { } f) EditRequested?.Invoke(f); });
        menu.Items.Add("Remove", null, (_, _) => RemoveSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Find next matching", null, (_, _) => { if (SelectedFilter is { } f) FindFilterRequested?.Invoke(f, true); }) { ShortcutKeyDisplayString = "F4" });
        menu.Items.Add(new ToolStripMenuItem("Find previous matching", null, (_, _) => { if (SelectedFilter is { } f) FindFilterRequested?.Invoke(f, false); }) { ShortcutKeyDisplayString = "Shift+F4" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Enable all", null, (_, _) => SetAllEnabled(true));
        menu.Items.Add("Disable all", null, (_, _) => SetAllEnabled(false));
        menu.Items.Add("Remove all", null, (_, _) => RemoveAll());
        return menu;
    }

    public void RemoveSelected()
    {
        if (_doc is null || SelectedFilter is not { } f) return;
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

    public void RemoveAll()
    {
        if (_doc is null) return;
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

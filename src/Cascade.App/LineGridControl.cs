using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>A viewport position that survives a rebuild of the visible-line set: the line to hold still,
/// its offset in rows from the top of the viewport, and the caret line to re-establish as rows shift.</summary>
public readonly record struct ViewAnchor(long Line, int Offset, long CaretLine)
{
    public static readonly ViewAnchor None = new(-1, 0, -1);
    public bool IsValid => Line >= 0;
}

/// <summary>
/// A fully virtualized, owner-drawn log view. Only the rows currently on screen are decoded and
/// painted (via GDI <see cref="TextRenderer"/>), so it renders multi-GB files at 60 fps with flat
/// memory. Draws a marker gutter, line-number gutter, per-filter colors (with dim mode), optional
/// columns, selection, and a caret; supports keyboard navigation, marker toggles and copy.
/// </summary>
public sealed class LineGridControl : Control
{
    private const int DefaultColumnWidth = 160;
    private const long CopyLineCap = 2_000_000;

    private readonly VScrollBar _vbar = new() { Dock = DockStyle.Right };
    private readonly HScrollBar _hbar = new() { Dock = DockStyle.Bottom };
    private readonly RowSelection _sel = new();
    private readonly List<ColumnValue> _cols = new();

    private CascadeDocument? _doc;
    private AppSettings _settings = new();

    private Font _fontRegular = null!, _fontBold = null!, _fontItalic = null!, _fontBoldItalic = null!;
    private int _rowHeight = 16;
    private int _charWidth = 8;

    private long _firstRow;
    private int _hScroll;
    private int _maxContentWidth;
    private long _caretRow = -1;
    private bool _dragging;
    // View stabilization across streaming view rebuilds: hold _anchorLine at _anchorOffset rows from the top
    // of the viewport, and keep the caret on _anchorCaretLine, as rows are discovered beneath them.
    private long _anchorLine = -1;
    private int _anchorOffset;
    private long _anchorCaretLine = -1;
    private bool _anchorSelect;
    private bool _syncingScroll;     // true while pushing _firstRow into the scrollbar (ignore its echo)
    private long[] _window = new long[64];   // file lines resolved for the current frame

    public event Action? SelectionChanged;
    public event Action<long>? LineDoubleClicked;
    public event Action? ZoomChanged;

    /// <summary>Raised with the 0-based marker index when marker navigation runs off the end. The host
    /// decides how to report it, so all the find commands give identical feedback.</summary>
    public event Action<int>? NoMoreMarkers;

    public LineGridControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Controls.Add(_vbar);
        Controls.Add(_hbar);
        _vbar.Scroll += (_, e) => { ClearViewAnchor(); _firstRow = e.NewValue; Invalidate(); };
        // Setting Value programmatically - which is what an accessibility tool or UI automation does, since
        // Scroll only fires for a physical drag - is just as much a deliberate move as dragging the thumb,
        // so it must drop the view anchor too. Without that, the next refresh re-applies the anchor and
        // snaps the view straight back, and scrolling appears to do nothing at all.
        _vbar.ValueChanged += (_, _) =>
        {
            if (_syncingScroll) return;
            ClearViewAnchor();
            _firstRow = _vbar.Value;
            Invalidate();
        };
        _hbar.Scroll += (_, e) => { _hScroll = e.NewValue; Invalidate(); };
        _hbar.ValueChanged += (_, _) => { _hScroll = _hbar.Value; Invalidate(); };
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Cascade log view";
        RebuildFonts();
    }

    public long CaretLine => _caretRow >= 0 && _doc is not null ? _doc.RowToLine(_caretRow) : -1;

    /// <summary>Captures the viewport's current position so it can be restored after the visible-line set
    /// changes: the line to hold still (the caret line when it is on screen, else the top visible line), its
    /// offset from the top of the viewport, and the caret line to re-establish. Call this BEFORE the change
    /// and pass the result to <see cref="SetViewAnchor"/> after it.</summary>
    public ViewAnchor CaptureViewAnchor()
    {
        if (_doc is null || _doc.RowCount == 0) return ViewAnchor.None;
        long rows = _doc.RowCount;
        long top = Math.Clamp(_firstRow, 0, rows - 1);
        long caretLine = _caretRow >= 0 && _caretRow < rows ? _doc.RowToLine(_caretRow) : -1;
        // Hold the caret line still when it is actually on screen; otherwise hold the top visible line, so
        // the text never jumps to a caret the user cannot see.
        bool caretOnScreen = _caretRow >= top && _caretRow < Math.Min(rows, top + VisibleRowCount);
        long pin = caretOnScreen ? _caretRow : top;
        return new ViewAnchor(_doc.RowToLine(pin), (int)(pin - top), caretLine);
    }

    /// <summary>Arms view stabilization: as the filtered view streams in, the anchor's line is held at the
    /// same on-screen offset, so lines discovered before it move the scrollbar rather than the text.
    /// Re-applied on each <see cref="RefreshView"/> until cleared.</summary>
    public void SetViewAnchor(ViewAnchor anchor, bool select)
    {
        _anchorLine = anchor.Line;
        _anchorCaretLine = anchor.CaretLine;
        _anchorOffset = Math.Clamp(anchor.Offset, 0, Math.Max(0, VisibleRowCount - 1));
        _anchorSelect = select;
    }

    public void ClearViewAnchor() { _anchorLine = -1; _anchorCaretLine = -1; }

    /// <summary>While a filter pass is running the viewport must be identified by a <b>line</b>, never by a bare
    /// row index: rows shift continuously as lines are added and dropped before it. Every user navigation
    /// (scroll, wheel, click, arrow keys) clears the anchor, so re-establish one at wherever the user just
    /// landed — otherwise the view starts drifting under them again the moment they scroll.</summary>
    private void AnchorToViewportIfStreaming()
    {
        if (_doc is null || _anchorLine >= 0 || !_doc.IsBusy) return;
        long rows = _doc.RowCount;
        if (rows == 0) return;
        _anchorLine = _doc.RowToLine(Math.Clamp(_firstRow, 0, rows - 1));
        _anchorOffset = 0;
        _anchorCaretLine = _caretRow >= 0 && _caretRow < rows ? _doc.RowToLine(_caretRow) : -1;
        // Keep a single selected row on its line; leave a multi-row selection alone.
        _anchorSelect = _sel.Count == 1;
    }

    /// <summary>Row currently displaying <paramref name="line"/> (or the nearest following visible line), or
    /// -1 when the streaming filter has not reached that line yet — its position is then simply unknown, and
    /// snapping the view to the scan frontier is what makes the text scroll wildly while filtering runs.</summary>
    private long ResolveRow(long line)
    {
        if (_doc is null || line < 0) return -1;
        long row = _doc.RowForLine(line);
        if (row >= 0) return row;
        return line < _doc.ViewKnownThroughLine ? _doc.RowAtOrAfterLine(line) : -1;
    }

    private void ApplyViewAnchor() => PinToAnchor();

    /// <summary>Re-derives the viewport (and caret) from the anchored <b>line</b>. Row indices move underneath
    /// us while a pass adds or drops lines before the anchor, so anything computed from them goes stale within
    /// a frame; painting re-resolves the window itself (see <see cref="OnPaint"/>), and this keeps the
    /// scrollbar and hit-testing in step between paints.</summary>
    private void PinToAnchor()
    {
        if (_doc is null || _anchorLine < 0) return;
        long rows = _doc.RowCount;
        if (rows == 0) return;

        PinCaretToAnchor();
        SyncFirstRowToAnchor();
    }

    /// <summary>Re-derives only the top row from the anchored line — no caret or selection side effects, so it
    /// is safe to call from hit-testing and from the middle of a drag-selection.</summary>
    private void SyncFirstRowToAnchor()
    {
        if (_doc is null || _anchorLine < 0) return;
        long rows = _doc.RowCount;
        if (rows == 0) return;
        long row = ResolveRow(_anchorLine);
        if (row < 0) return; // position not knowable yet → hold the viewport perfectly still
        _firstRow = ClampFirstRow(Math.Clamp(row, 0, rows - 1) - _anchorOffset);
    }

    /// <summary>Keeps the caret (and, in filtered mode, the selection) on its original line as rows shift.</summary>
    private void PinCaretToAnchor()
    {
        if (_doc is null || _anchorCaretLine < 0) return;
        long rows = _doc.RowCount;
        long caret = ResolveRow(_anchorCaretLine);
        if (caret < 0 || rows == 0) return;
        _caretRow = Math.Clamp(caret, 0, rows - 1);
        if (_anchorSelect) _sel.SetSingle(_caretRow);
    }

    private long ClampFirstRow(long first)
    {
        long rows = _doc?.RowCount ?? 0;
        return Math.Clamp(first, 0, Math.Max(0, rows - VisibleRowCount));
    }

    /// <summary>Scrolls so <paramref name="first"/> is the top visible row (clamped), syncing the scrollbar.</summary>
    private void SetFirstRow(long first)
    {
        _firstRow = ClampFirstRow(first);
        SyncVScrollValue();
    }

    /// <summary>Pushes <see cref="_firstRow"/> into the scrollbar without letting the scrollbar's own clamping
    /// (its Maximum/LargeChange keep growing as rows stream in) drag the visible text back.</summary>
    private void SyncVScrollValue()
    {
        _syncingScroll = true;
        try
        {
            int max = Math.Max(_vbar.Minimum, _vbar.Maximum - _vbar.LargeChange + 1);
            _vbar.Value = (int)Math.Clamp(_firstRow, _vbar.Minimum, max);
        }
        finally { _syncingScroll = false; }
    }

    public void Attach(CascadeDocument doc, AppSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _firstRow = 0;
        _hScroll = 0;
        _caretRow = -1;
        _sel.Clear();
        ClearViewAnchor();
        RebuildFonts();
        RefreshView();
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        RebuildFonts();
        RefreshView();
    }

    public void RebuildFonts()
    {
        _fontRegular?.Dispose(); _fontBold?.Dispose(); _fontItalic?.Dispose(); _fontBoldItalic?.Dispose();
        float size = _settings.EffectiveFontSize;
        FontFamily family;
        try { family = new FontFamily(_settings.FontFamily); }
        catch { family = FontFamily.GenericMonospace; }
        _fontRegular = new Font(family, size, FontStyle.Regular);
        _fontBold = new Font(family, size, FontStyle.Bold);
        _fontItalic = new Font(family, size, FontStyle.Italic);
        _fontBoldItalic = new Font(family, size, FontStyle.Bold | FontStyle.Italic);
        _rowHeight = Math.Max(_fontRegular.Height + 2, 8);
        _charWidth = Math.Max(1, TextRenderer.MeasureText("0", _fontRegular, new Size(1000, 100),
            TextFormatFlags.NoPadding).Width);
        Invalidate();
    }

    /// <summary>Recomputes scrollbar ranges from the document and repaints. Call (on the UI thread)
    /// whenever counts change or the view mode/filters change.</summary>
    public void RefreshView()
    {
        long rows = _doc?.RowCount ?? 0;
        int visible = VisibleRowCount;

        long maxFirst = Math.Max(0, rows - visible);
        if (_firstRow > maxFirst) _firstRow = maxFirst;
        if (_caretRow >= rows) _caretRow = rows - 1;

        int vMax = (int)Math.Min(int.MaxValue, Math.Max(0, rows - 1));
        // Grow the scrollbar's range silently: while filtering streams, Maximum/LargeChange change on every
        // refresh, and letting the scrollbar echo that back into _firstRow would drag the visible text.
        _syncingScroll = true;
        try
        {
            _vbar.Maximum = vMax;
            _vbar.LargeChange = Math.Max(1, visible);
            _vbar.SmallChange = 1;
            _vbar.Enabled = rows > visible;
        }
        finally { _syncingScroll = false; }

        UpdateHScroll();
        AnchorToViewportIfStreaming();
        if (_anchorLine >= 0) ApplyViewAnchor();
        SyncVScrollValue(); // reflect the final position in the (possibly grown) range
        Invalidate();
    }

    private void UpdateHScroll()
    {
        int viewport = Math.Max(1, ContentWidth);
        int max = Math.Max(_maxContentWidth, viewport);
        _hbar.Maximum = max;
        _hbar.LargeChange = viewport;
        _hbar.SmallChange = _charWidth * 4;
        if (_hScroll > max - viewport) _hScroll = Math.Max(0, max - viewport);
        _hbar.Value = Math.Clamp(_hScroll, 0, Math.Max(0, _hbar.Maximum - _hbar.LargeChange + 1));
        _hbar.Enabled = _maxContentWidth > viewport;
    }

    private int ContentWidth => Math.Max(0, ClientSize.Width - _vbar.Width - GutterWidth());
    private int HeaderHeight => (_doc?.Columns.Enabled ?? false) ? _rowHeight : 0;
    private int VisibleRowCount => Math.Max(1, (ClientSize.Height - _hbar.Height - HeaderHeight) / Math.Max(1, _rowHeight));

    private bool MarkersVisible =>
        _doc is not null && _settings.MarkerVisibility switch
        {
            MarkerVisibilityMode.Always => true,
            MarkerVisibilityMode.Never => false,
            _ => _doc.Markers.AnyInUse
        };

    private int MarkerGutterWidth => MarkersVisible ? 8 * 5 + 6 : 0;

    private int LineNumberGutterWidth
    {
        get
        {
            if (!_settings.ShowLineNumbers || _doc is null) return 0;
            long max = Math.Max(1, _doc.CompletedLineCount);
            int digits = max.ToString().Length;
            return digits * _charWidth + 12;
        }
    }

    private int GutterWidth() => MarkerGutterWidth + LineNumberGutterWidth;

    /// <summary>
    /// Flags for every piece of scrolling line text.
    ///
    /// <see cref="TextFormatFlags.PreserveGraphicsClipping"/> is the load-bearing one: TextRenderer draws
    /// through GDI, which ignores the GDI+ clip region set on the Graphics unless it is asked not to. Without
    /// it, a line scrolled right starts at a negative x and is painted straight over the marker and
    /// line-number margins - the SetClip around the call looks like it should prevent that, and does not.
    /// </summary>
    private const TextFormatFlags TextFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;

    /// <summary>Width of the marker + line-number margin, for harnesses that check nothing paints over it.</summary>
    internal int GutterWidthForTesting => GutterWidth();

    /// <summary>The topmost display row, so a harness can tell where the view actually ended up.</summary>
    internal long FirstRowForTesting => _firstRow;

    /// <summary>Drives the vertical scrollbar the way UI Automation does: by setting Value, which raises
    /// ValueChanged but never Scroll.</summary>
    internal void SetVerticalScrollValue(int firstRow) => _vbar.Value = firstRow;

    /// <summary>
    /// The margin that horizontal scrolling must leave pixel-identical: the marker and line-number columns,
    /// excluding the column header and the horizontal scrollbar - that scrollbar's thumb sits under the
    /// margin and does of course move when you scroll.
    /// </summary>
    internal Rectangle GutterAreaForTesting =>
        new(0, HeaderHeight, GutterWidth(), Math.Max(0, ClientSize.Height - HeaderHeight - _hbar.Height));

    /// <summary>Scrolls the view horizontally, as dragging the horizontal scrollbar does.</summary>
    internal void ScrollHorizontallyTo(int x) => SetHScroll(x);

    /// <summary>The furthest right the view can go: the scrollbar's own limit, so it can never be driven
    /// past the longest line currently measured.</summary>
    private int MaxHScroll => Math.Max(0, _hbar.Maximum - _hbar.LargeChange + 1);

    /// <summary>The single way the view scrolls sideways - clamped, and with the scrollbar kept in step so
    /// the thumb never disagrees with what is drawn.</summary>
    private void SetHScroll(int x)
    {
        int clamped = Math.Clamp(x, 0, MaxHScroll);
        if (clamped == _hScroll) return;
        _hScroll = clamped;
        _hbar.Value = clamped;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_settings.Background);
        if (_doc is null) { DrawFocusBar(g); return; }

        int gutter = GutterWidth();
        int contentW = ContentWidth;
        int headerH = HeaderHeight;
        long rows = _doc.RowCount;
        int visible = VisibleRowCount;

        // Resolve this whole frame in one shot against a single snapshot of the visible set. While a filter
        // pass streams, lines before the viewport are being added and dropped continuously, so a first row
        // computed on the last refresh tick is already stale, and row-by-row lookups would mix two states
        // inside a single frame. Doing it here keeps the anchored line exactly where the user last saw it.
        if (_window.Length < visible) _window = new long[visible];
        Span<long> window = _window.AsSpan(0, visible);
        int windowCount;
        AnchorToViewportIfStreaming();
        if (_anchorLine >= 0)
        {
            PinCaretToAnchor();
            _firstRow = ClampFirstRow(_doc.ResolveWindow(_anchorLine, _anchorOffset, window, out windowCount));
        }
        else windowCount = _doc.LinesForRows(_firstRow, window);

        var defaults = new ResolvedStyle(ToRgb(_settings.Foreground), ToRgb(_settings.Background), false, false);

        bool columns = _doc.Columns.Enabled;
        var splitter = columns ? new ColumnSplitter(_doc.Columns) : null;
        int runningMaxWidth = 0;

        if (columns) DrawColumnHeader(g, gutter, contentW);

        for (int i = 0; i < visible; i++)
        {
            long row = _firstRow + i;
            if (row >= rows || i >= windowCount) break;
            int y = headerH + i * _rowHeight;
            long line = _window[i];
            string text = _doc.GetLineText(line);
            var eval = _doc.EvaluateText(text, line);

            ResolvedStyle style = eval.ColorFilter is not null
                ? StyleResolver.Resolve(eval.ColorFilter, defaults)
                : defaults;

            bool selected = _sel.Contains(row);
            bool dim = !_doc.FilteredMode && !eval.Shown;

            Color back = selected ? _settings.SelectionBack : ToColor(style.Background);
            Color fore = selected ? _settings.SelectionFore : (dim ? _settings.DimForeground : ToColor(style.Foreground));

            var rowRect = new Rectangle(0, y, ClientSize.Width - _vbar.Width, _rowHeight);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, rowRect);

            DrawMarkers(g, line, y);
            DrawLineNumber(g, line, y, selected);

            Font font = SelectFont(style);
            var contentRect = new Rectangle(gutter, y, contentW, _rowHeight);
            var clip = g.Clip;
            g.SetClip(contentRect);
            if (columns && splitter is not null)
                runningMaxWidth = Math.Max(runningMaxWidth, DrawColumns(g, splitter, text, gutter, y, fore, font));
            else
                runningMaxWidth = Math.Max(runningMaxWidth, DrawFullLine(g, text, gutter, y, fore, font));
            g.Clip = clip;

            if (_doc.IsLineTruncated(line))
                TextRenderer.DrawText(g, " […]", _fontItalic,
                    new Point(ClientSize.Width - _vbar.Width - 40, y), Color.Gray);

            if (row == _caretRow && Focused)
                using (var pen = new Pen(Color.FromArgb(120, _settings.SelectionBack))) g.DrawRectangle(pen, 0, y, rowRect.Width - 1, _rowHeight - 1);
        }

        DrawFocusBar(g);

        if (columns) runningMaxWidth = TotalColumnsWidth();
        if (runningMaxWidth != _maxContentWidth)
        {
            _maxContentWidth = runningMaxWidth;
            BeginInvoke(UpdateHScroll);
        }
    }

    private void DrawFocusBar(Graphics g)
    {
        if (!Focused) return;
        using var b = new SolidBrush(_settings.SelectionBack);
        g.FillRectangle(b, 0, 0, LogicalToDeviceUnits(3), ClientSize.Height);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private Font SelectFont(ResolvedStyle s) =>
        s is { Bold: true, Italic: true } ? _fontBoldItalic :
        s.Bold ? _fontBold :
        s.Italic ? _fontItalic : _fontRegular;

    private void DrawColumnHeader(Graphics g, int gutter, int contentW)
    {
        var rect = new Rectangle(0, 0, ClientSize.Width - _vbar.Width, _rowHeight);
        using (var b = new SolidBrush(_settings.GutterBack)) g.FillRectangle(b, rect);
        using (var pen = new Pen(Color.FromArgb(210, 210, 210))) g.DrawLine(pen, 0, _rowHeight - 1, rect.Width, _rowHeight - 1);
        int x = gutter - _hScroll;
        var clip = g.Clip;
        g.SetClip(new Rectangle(gutter, 0, contentW, _rowHeight));
        foreach (var def in _doc!.Columns.Columns)
        {
            if (!def.Visible) continue;
            int w = def.Width > 0 ? def.Width : DefaultColumnWidth;
            TextRenderer.DrawText(g, def.Name, _fontBold, new Rectangle(x + 3, 1, w - 6, _rowHeight - 2),
                Color.FromArgb(80, 80, 80), TextFlags | TextFormatFlags.EndEllipsis);
            x += w;
        }
        g.Clip = clip;
    }

    private int TotalColumnsWidth()
    {
        int w = 0;
        foreach (var def in _doc!.Columns.Columns) if (def.Visible) w += def.Width > 0 ? def.Width : DefaultColumnWidth;
        return w;
    }

    private int DrawColumns(Graphics g, ColumnSplitter splitter, string text, int gutter, int y, Color fore, Font font)
    {
        splitter.Split(text, _cols);
        int x = gutter - _hScroll;
        var flags = TextFlags | TextFormatFlags.EndEllipsis | TextFormatFlags.Left;
        var spec = _doc!.Columns;
        for (int i = 0; i < spec.Columns.Count; i++)
        {
            var def = spec.Columns[i];
            if (!def.Visible) continue;
            int w = def.Width > 0 ? def.Width : DefaultColumnWidth;
            string val = i < _cols.Count ? text.Substring(_cols[i].Start, _cols[i].Length) : "";
            var cellFlags = def.Align == ColumnAlign.Right ? flags | TextFormatFlags.Right
                          : def.Align == ColumnAlign.Center ? flags | TextFormatFlags.HorizontalCenter : flags;
            TextRenderer.DrawText(g, val, font, new Rectangle(x + 3, y, w - 6, _rowHeight), fore, cellFlags);
            x += w;
        }
        return TotalColumnsWidth();
    }

    private int DrawFullLine(Graphics g, string text, int gutter, int y, Color fore, Font font)
    {
        if (_settings.TabSize > 0 && text.IndexOf('\t') >= 0)
            text = text.Replace("\t", new string(' ', _settings.TabSize));
        var pt = new Point(gutter - _hScroll, y);
        TextRenderer.DrawText(g, text, font, pt, fore, TextFlags);
        int w = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, _rowHeight), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        return w + 8;
    }

    private void DrawMarkers(Graphics g, long line, int y)
    {
        if (!MarkersVisible || _doc is null) return;
        // Keep the marker gutter the neutral margin color (not the line's fill color) so the marker
        // bars stay clearly visible regardless of the line's filter highlight or selection.
        using (var bg = new SolidBrush(_settings.GutterBack))
            g.FillRectangle(bg, 0, y, MarkerGutterWidth, _rowHeight);
        byte mask = _doc.Markers.MaskOf(line);
        if (mask == 0) return;
        for (int m = 0; m < 8; m++)
        {
            if ((mask & (1 << m)) == 0) continue;
            using var b = new SolidBrush(AppSettings.MarkerColors[m]);
            g.FillRectangle(b, 3 + m * 5, y + 2, 4, _rowHeight - 4);
        }
    }

    private void DrawLineNumber(Graphics g, long line, int y, bool selected)
    {
        int lnw = LineNumberGutterWidth;
        if (lnw == 0) return;
        int x = MarkerGutterWidth;
        var rect = new Rectangle(x, y, lnw, _rowHeight);
        using (var b = new SolidBrush(_settings.GutterBack)) g.FillRectangle(b, rect);
        var color = selected ? _settings.SelectionBack : _settings.LineNumberColor;
        TextRenderer.DrawText(g, (line + 1).ToString(), _fontRegular, new Rectangle(x, y, lnw - 6, _rowHeight),
            color, TextFormatFlags.NoPadding | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    // ---- input ----

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (_doc is null || e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
        long row = RowAtY(e.Y); // resolves against the live viewport before stabilization is dropped
        ClearViewAnchor();
        if (row < 0 || row >= _doc.RowCount) return;

        if ((ModifierKeys & Keys.Shift) != 0 && _sel.Anchor >= 0) _sel.SetRange(_sel.Anchor, row);
        else if ((ModifierKeys & Keys.Control) != 0) _sel.ToggleSingle(row);
        else _sel.SetSingle(row);

        _caretRow = row;
        _dragging = true;
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging && _doc is not null)
        {
            long row = Math.Clamp(RowAtY(e.Y), 0, Math.Max(0, _doc.RowCount - 1));
            if (row != _caretRow)
            {
                _sel.SetRange(_sel.Anchor, row);
                _caretRow = row;
                EnsureVisible(row);
                Invalidate();
                SelectionChanged?.Invoke();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (_doc is not null)
        {
            long row = RowAtY(e.Y);
            if (row >= 0 && row < _doc.RowCount) LineDoubleClicked?.Invoke(_doc.RowToLine(row));
        }
        base.OnMouseDoubleClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            Zoom(e.Delta > 0 ? 10 : -10);
            return;
        }
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            _hScroll = Math.Clamp(_hScroll - Math.Sign(e.Delta) * _charWidth * 6, 0,
                Math.Max(0, _hbar.Maximum - _hbar.LargeChange + 1));
            _hbar.Value = _hScroll;
            Invalidate();
            return;
        }
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines <= 0) lines = 3;
        ScrollBy(-Math.Sign(e.Delta) * lines);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_doc is null) { base.OnKeyDown(e); return; }
        long rows = _doc.RowCount;
        int page = VisibleRowCount - 1;

        switch (e.KeyCode)
        {
            // Ctrl+Up/Down scrolls the view only - the caret and selection stay where they are, so you can
            // look around without losing your place. Must come before the plain Up/Down cases.
            case Keys.Up when e.Control && !e.Shift && !e.Alt: ScrollBy(-1); break;
            case Keys.Down when e.Control && !e.Shift && !e.Alt: ScrollBy(1); break;
            case Keys.Up: MoveCaret(-1, e.Shift); break;
            case Keys.Down: MoveCaret(1, e.Shift); break;
            case Keys.PageUp: MoveCaret(-page, e.Shift); break;
            case Keys.PageDown: MoveCaret(page, e.Shift); break;
            case Keys.Home when e.Control: MoveCaretTo(0, e.Shift); break;
            case Keys.End when e.Control: MoveCaretTo(rows - 1, e.Shift); break;
            // Plain Home/End jump the view to the far left and far right of the longest line on screen.
            case Keys.Home: SetHScroll(0); break;
            case Keys.End: SetHScroll(MaxHScroll); break;
            case Keys.Left: SetHScroll(_hScroll - _charWidth * 4); break;
            case Keys.Right: SetHScroll(_hScroll + _charWidth * 4); break;
            case Keys.A when e.Control: _sel.SelectAll(rows); Invalidate(); SelectionChanged?.Invoke(); break;
            case Keys.C when e.Control: CopySelection(false); break;
            case >= Keys.D1 and <= Keys.D8 when e.Control: ToggleMarker(e.KeyCode - Keys.D1); break;
            case >= Keys.D1 and <= Keys.D8: NavigateMarker(e.KeyCode - Keys.D1, !e.Shift); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    private void ToggleMarker(int index)
    {
        if (_doc is null) return;
        foreach (long row in _sel.Rows(CopyLineCap)) _doc.Markers.Toggle(_doc.RowToLine(row), index);
        if (_sel.Count == 0 && _caretRow >= 0) _doc.Markers.Toggle(_doc.RowToLine(_caretRow), index);
        // Re-run filtering only when a marker-based filter is active (its matches depend on markers).
        // Otherwise a marker toggle just changes the gutter, so a repaint suffices — re-filtering would
        // needlessly rebuild the view and shift the scroll position / selection.
        if (_doc.CurrentSnapshot.HasMarkerFilter) _doc.ApplyFilters();
        RefreshView();
    }

    private void NavigateMarker(int index, bool forward)
    {
        if (_doc is null) return;
        _anchorLine = -1;
        long fromLine = CaretLine < 0 ? (forward ? -1 : _doc.CompletedLineCount) : CaretLine;
        long line = forward ? _doc.Markers.Next(fromLine, index) : _doc.Markers.Previous(fromLine, index);
        if (line < 0) { NoMoreMarkers?.Invoke(index); return; }
        long row = _doc.RowForLine(line);
        if (row < 0) row = _doc.RowAtOrAfterLine(line);
        _caretRow = row;
        _sel.SetSingle(row);
        EnsureVisible(row);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private void MoveCaret(long delta, bool extend)
    {
        SyncFirstRowToAnchor(); // work from the current position, not one a streaming pass has already shifted
        MoveCaretTo((_caretRow < 0 ? _firstRow : _caretRow) + delta, extend);
    }

    private void MoveCaretTo(long row, bool extend)
    {
        if (_doc is null) return;
        _anchorLine = -1;
        row = Math.Clamp(row, 0, Math.Max(0, _doc.RowCount - 1));
        _caretRow = row;
        if (extend && _sel.Anchor >= 0) _sel.SetRange(_sel.Anchor, row);
        else _sel.SetSingle(row);
        EnsureVisible(row);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private long RowAtY(int y)
    {
        if (y < HeaderHeight) return -1;
        // A running pass shifts every row index between paints, so re-derive the top row from its anchored
        // line first: otherwise a click maps to whatever was under the cursor a frame (thousands of rows) ago.
        SyncFirstRowToAnchor();
        return _firstRow + (y - HeaderHeight) / _rowHeight;
    }

    private void ScrollBy(int deltaRows)
    {
        // Scroll relative to where the view actually is now, not to a row index captured a frame ago (which a
        // running pass may already have shifted by thousands of rows).
        SyncFirstRowToAnchor();
        ClearViewAnchor();
        SetFirstRow(_firstRow + deltaRows);
        Invalidate();
    }

    private void EnsureVisible(long row)
    {
        int visible = VisibleRowCount;
        if (row < _firstRow) SetFirstRow(row);
        else if (row >= _firstRow + visible) SetFirstRow(row - visible + 1);
    }

    public void SelectAll() { if (_doc is not null) { _sel.SelectAll(_doc.RowCount); Invalidate(); SelectionChanged?.Invoke(); } }

    /// <summary>Clears the current selection (used when the visible row set changes).</summary>
    public void ClearSelection() { _sel.Clear(); Invalidate(); SelectionChanged?.Invoke(); }

    public long SelectedCount => _sel.Count;

    public void Zoom(int deltaPercent)
    {
        _settings.ZoomPercent = Math.Clamp(_settings.ZoomPercent + deltaPercent, 40, 400);
        RebuildFonts();
        RefreshView();
        ZoomChanged?.Invoke();
    }

    public void ResetZoom() { _settings.ZoomPercent = 100; RebuildFonts(); RefreshView(); ZoomChanged?.Invoke(); }

    /// <summary>Scrolls to and selects the given file line (mapped to the nearest visible row).</summary>
    public void GoToLine(long line)
    {
        if (_doc is null) return;
        _anchorLine = -1;
        long row = _doc.RowForLine(line);
        if (row < 0) row = _doc.RowAtOrAfterLine(line);
        row = Math.Clamp(row, 0, Math.Max(0, _doc.RowCount - 1));
        _caretRow = row;
        _sel.SetSingle(row);
        EnsureVisible(row);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    public void CopySelection(bool withLineNumbers)
    {
        if (_doc is null || _sel.Count == 0) return;
        var sb = new StringBuilder();
        long n = 0;
        foreach (long row in _sel.Rows(CopyLineCap))
        {
            long line = _doc.RowToLine(row);
            if (withLineNumbers) sb.Append(line + 1).Append('\t');
            sb.AppendLine(_doc.GetLineText(line));
            if (++n >= CopyLineCap) break;
        }
        if (sb.Length > 0)
            try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard busy */ }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); RefreshView(); }

    // ---- accessibility (UI Automation) ----
    // The log view is fully owner-drawn, so it exposes a proper accessibility tree: the control is a
    // List whose children are the currently-visible lines. Each line reports its 1-based number
    // (Value), its text (Name), selection/focus state, and on-screen bounds. This gives screen readers
    // a usable view of the log AND lets external UI-automation tests observe selection and scrolling.
    protected override AccessibleObject CreateAccessibilityInstance() => new GridAccessibleObject(this);

    private int VisibleRowSpan()
    {
        if (_doc is null) return 0;
        long rows = _doc.RowCount;
        return (int)Math.Max(0, Math.Min(VisibleRowCount, rows - _firstRow));
    }

    /// <summary>Selects a display row in response to an accessibility client (screen reader / UIA).
    /// Marshalled to the UI thread; behaves like a single-click selection.</summary>
    internal void SelectRowForAccessibility(long row)
    {
        if (_doc is null) return;
        if (InvokeRequired) { BeginInvoke(() => SelectRowForAccessibility(row)); return; }
        row = Math.Clamp(row, 0, Math.Max(0, _doc.RowCount - 1));
        _anchorLine = -1;
        _caretRow = row;
        _sel.SetSingle(row);
        EnsureVisible(row);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private sealed class GridAccessibleObject : Control.ControlAccessibleObject
    {
        private readonly LineGridControl _g;
        public GridAccessibleObject(LineGridControl g) : base(g) => _g = g;

        public override AccessibleRole Role => AccessibleRole.List;

        public override string? Value
        {
            get
            {
                if (_g._doc is null || _g._caretRow < 0 || _g._caretRow >= _g._doc.RowCount) return "";
                long line = _g._doc.RowToLine(_g._caretRow);
                return $"Line {line + 1}: {_g._doc.GetLineText(line)}";
            }
            set { }
        }

        public override int GetChildCount() => _g.VisibleRowSpan();

        // UIA walks a tree by asking a parent for its children and then matching identity to work out
        // where it is, so handing back a fresh object per call turns a walk of N rows into an O(N^2)
        // scan - measured at 1.2s to enumerate 54 rows, which dominated the whole UI test suite. One
        // object per visible slot fixes that; each still resolves its row from _firstRow on demand, so
        // the cache stays correct while scrolling. Clients call in on RPC threads, hence the concurrent
        // dictionary.
        private readonly ConcurrentDictionary<int, RowAccessibleObject> _rows = new();

        public override AccessibleObject? GetChild(int index)
            => index >= 0 && index < _g.VisibleRowSpan()
                ? _rows.GetOrAdd(index, i => new RowAccessibleObject(_g, this, i))
                : null;

        public override AccessibleObject? GetSelected()
        {
            if (_g._caretRow < 0) return null;
            int i = (int)(_g._caretRow - _g._firstRow);
            return GetChild(i);
        }

        public override AccessibleObject? GetFocused() => GetSelected();

        public override AccessibleObject? HitTest(int x, int y)
        {
            Point client = _g.PointToClient(new Point(x, y));
            int i = (client.Y - _g.HeaderHeight) / Math.Max(1, _g._rowHeight);
            return i >= 0 && i < _g.VisibleRowSpan() ? GetChild(i) : this;
        }
    }

    private sealed class RowAccessibleObject : AccessibleObject
    {
        private readonly LineGridControl _g;
        private readonly AccessibleObject _parent;
        private readonly int _visibleIndex;

        public RowAccessibleObject(LineGridControl g, AccessibleObject parent, int visibleIndex)
        {
            _g = g; _parent = parent; _visibleIndex = visibleIndex;
        }

        private long Row => _g._firstRow + _visibleIndex;
        private long Line => _g._doc is null ? -1 : _g._doc.RowToLine(Row);

        public override AccessibleObject Parent => _parent;
        public override AccessibleRole Role => AccessibleRole.ListItem;

        // Rows are leaves, and they know their own position - answering navigation directly saves the
        // bridge from scanning the parent's children to work out where this row sits.
        public override int GetChildCount() => 0;

        public override AccessibleObject? Navigate(AccessibleNavigation direction) => direction switch
        {
            AccessibleNavigation.Previous or AccessibleNavigation.Up or AccessibleNavigation.Left
                => _parent.GetChild(_visibleIndex - 1),
            AccessibleNavigation.Next or AccessibleNavigation.Down or AccessibleNavigation.Right
                => _parent.GetChild(_visibleIndex + 1),
            AccessibleNavigation.FirstChild or AccessibleNavigation.LastChild => null,
            _ => base.Navigate(direction)
        };

        public override string? Name
        {
            get { long line = Line; return line < 0 || _g._doc is null ? "" : _g._doc.GetLineText(line); }
            set { }
        }

        // 1-based file line number, so screen readers and automation can identify the row.
        public override string? Value { get => (Line + 1).ToString(); set { } }

        public override Rectangle Bounds
        {
            get
            {
                int y = _g.HeaderHeight + _visibleIndex * _g._rowHeight;
                int w = Math.Max(0, _g.ClientSize.Width - _g._vbar.Width);
                return _g.RectangleToScreen(new Rectangle(0, y, w, _g._rowHeight));
            }
        }

        public override AccessibleStates State
        {
            get
            {
                var s = AccessibleStates.Selectable | AccessibleStates.Focusable;
                if (_g._sel.Contains(Row)) s |= AccessibleStates.Selected;
                if (Row == _g._caretRow) s |= AccessibleStates.Focused;
                return s;
            }
        }

        public override void Select(AccessibleSelection flags)
        {
            if ((flags & (AccessibleSelection.TakeSelection | AccessibleSelection.TakeFocus)) != 0)
                _g.SelectRowForAccessibility(Row);
        }

        public override void DoDefaultAction() => _g.SelectRowForAccessibility(Row);
    }

    private static RgbColor ToRgb(Color c) => new(c.R, c.G, c.B);
    private static Color ToColor(RgbColor c) => Color.FromArgb(c.R, c.G, c.B);
}

using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Find;
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

    private SlimScrollBar _hbar = null!;
    private SlimScrollBar _vbar = null!;
    private MiniMapControl? _map;
    private readonly RowSelection _sel = new();
    private readonly List<ColumnValue> _cols = new();

    private CascadeDocument? _doc;
    private AppSettings _settings = new();

    private Font _fontRegular = null!, _fontBold = null!, _fontItalic = null!, _fontBoldItalic = null!;
    private FontFamily? _fontFamily;
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
    private long[] _window = new long[64];   // file lines resolved for the current frame

    // Where each row was actually painted this frame. With word wrap a row is as tall as the number of
    // segments it broke into, so hit-testing, page keys and the accessible bounds all have to read the
    // layout rather than multiply by a fixed row height.
    private readonly List<(long Row, int Top, int Height, int Segments)> _layout = new();
    private readonly List<int> _segments = new();   // wrap points for the row being painted

    // Character selection within one row. There is no caret and none is drawn - nothing here is editable -
    // so this is purely a highlighted range that any navigation drops. Indices are into the row's DISPLAYED
    // text (tabs already expanded), which is what the hit test and the painting both work in.
    private long _charRow = -1;
    private int _charAnchor, _charFocus;
    private bool _charDragging;
    // Where a drag first took hold. Kept while the drag wanders onto other rows, which is what lets coming
    // back to that row go back to selecting characters on it.
    private long _charOriginRow = -1;
    private int _charOriginAt;
    private DateTime _lastClickAt;

    // Hover tip naming the filters that matched a line. Held off until the pointer has settled, so it never
    // flickers past while someone is just moving across the window.
    private readonly ToolTip _tips = new() { UseAnimation = false, UseFading = false };
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = HoverDelayMs };
    private const int HoverDelayMs = 600;
    private const int TipDurationMs = 20_000;
    private long _tipRow = -1;
    private Point _tipPoint;
    private Point _lastClickAtPoint;
    private int _clickCount;

    public event Action? SelectionChanged;
    /// <summary>Double-clicking a line asks for a filter to be written for it. Carries the part of the line
    /// that was picked out, or null to mean the whole of it.</summary>
    public event Action<string?>? NewFilterRequested;
    private string? _carriedSelection;
    public event Action? ZoomChanged;

    /// <summary>Raised with the 0-based marker index when marker navigation runs off the end. The host
    /// decides how to report it, so all the find commands give identical feedback.</summary>
    public event Action<int>? NoMoreMarkers;

    public LineGridControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        // Added in this order because docking runs from the end of the collection backwards: the scrollbar
        // and map take a full-height strip each, and the sideways bar gets what is left, so it stops short
        // of them - as a scrollbar pair does everywhere else - and they no longer shift up and down by its
        // height when wrapping is switched on and off.
        _hbar = new SlimScrollBar(this, vertical: false);
        Controls.Add(_hbar);
        _map = new MiniMapControl(this);
        Controls.Add(_map);
        _vbar = new SlimScrollBar(this);
        Controls.Add(_vbar);
        _vbar.Scrolled += v => { ClearViewAnchor(); _firstRow = v; Invalidate(); };
        _hbar.Scrolled += v => { _hScroll = (int)v; Invalidate(); };
        // The map is a child, so it is not repainted by the grid repainting - and everything it draws is a
        // picture of the grid's own state. One hook here rather than a call beside every Invalidate() in the
        // file, because one of those would eventually be forgotten.
        Invalidated += (_, _) => { _map?.SyncToGrid(); _vbar?.Invalidate(); };
        _tipTimer.Tick += (_, _) => ShowTipNow();
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Cascade log view";
        RebuildFonts();
    }

    public long CaretLine => _caretRow >= 0 && _doc is not null ? _doc.RowToLine(_caretRow) : -1;

    // ---- character selection ----

    /// <summary>Whether part of one line is selected, as opposed to whole lines.</summary>
    public bool HasCharSelection => _charRow >= 0 && _charFocus != _charAnchor;

    /// <summary>The selected part of a line, or null when the selection is whole lines. This is the
    /// displayed text, so a tab reads as the spaces it was shown as.</summary>
    public string? SelectedText
    {
        get
        {
            if (!HasCharSelection || _doc is null) return null;
            string text = DisplayText(_charRow);
            int from = Math.Clamp(Math.Min(_charAnchor, _charFocus), 0, text.Length);
            int to = Math.Clamp(Math.Max(_charAnchor, _charFocus), 0, text.Length);
            return to > from ? text[from..to] : null;
        }
    }

    /// <summary>Drops any part-of-a-line selection. Every way of moving around calls this: the range means
    /// nothing once the thing it was pointing at is no longer where the user is looking.</summary>
    private void ClearCharSelection()
    {
        if (_charRow < 0) return;
        _charRow = -1;
        _charAnchor = _charFocus = 0;
        Invalidate();
    }

    /// <summary>A row's text as it is drawn: tabs expanded, so a character index means the same thing to the
    /// hit test, the painting and the clipboard.</summary>
    private string DisplayText(long row)
    {
        if (_doc is null) return "";
        return Expand(_doc.GetLineText(_doc.RowToLine(row)));
    }

    /// <summary>Tabs as the spaces they are drawn as, so a character index means the same thing to the hit
    /// test, the painting and the clipboard.</summary>
    private string Expand(string text)
        => _settings.TabSize > 0 && text.Contains('\t')
            ? text.Replace("\t", new string(' ', _settings.TabSize))
            : text;

    /// <summary>Character index in a row's displayed text nearest to <paramref name="x"/>, by binary search
    /// on the measured width of the prefix - the same measurement the drawing uses, so the highlight lands
    /// exactly where the pointer did. <paramref name="y"/> picks the wrapped segment.</summary>
    private int CharIndexAt(long row, int x, int y)
    {
        string text = DisplayText(row);
        if (text.Length == 0) return 0;
        var font = FontForRow(row, text);
        (int from, int to) = SegmentAt(row, text, font, y);

        int left = GutterWidth() - (Wrapping ? 0 : _hScroll);
        int target = Math.Max(0, x - left);
        string part = text[from..to];

        int lo = 0, hi = part.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (PrefixWidth(part, mid, font) <= target) lo = mid; else hi = mid - 1;
        }
        // Round to the nearer edge of the character the pointer is inside, as a text box does.
        if (lo < part.Length)
        {
            int a = PrefixWidth(part, lo, font), b = PrefixWidth(part, lo + 1, font);
            if (target - a > b - target) lo++;
        }
        return from + lo;
    }

    /// <summary>The stretch of a line drawn at a given y. The whole line when nothing is wrapped.</summary>
    private (int From, int To) SegmentAt(long row, string text, Font font, int y)
    {
        if (!Wrapping) return (0, text.Length);
        int top = TextTop;
        foreach (var (r, rowTop, height, _) in _layout)
            if (r == row) { top = rowTop; break; }
        int index = Math.Max(0, (y - top) / Math.Max(1, _rowHeight));

        int count = WrapInto(text, ContentWidth, font, _segments);
        index = Math.Min(index, count - 1);
        int from = _segments[index];
        int to = index + 1 < _segments.Count ? _segments[index + 1] : text.Length;
        return (from, to);
    }

    /// <summary>The font a row is drawn in. Measuring with anything else would put the highlight in a
    /// slightly different place from the glyphs on a bold or italic filter row.</summary>
    private Font FontForRow(long row, string text)
    {
        if (_doc is null) return _fontRegular;
        var defaults = new ResolvedStyle(ToRgb(_settings.Foreground), ToRgb(_settings.Background), false, false);
        var eval = _doc.EvaluateText(text, _doc.RowToLine(row));
        return SelectFont(eval.ColorFilter is not null ? StyleResolver.Resolve(eval.ColorFilter, defaults) : defaults);
    }

    private int PrefixWidth(string text, int count, Font font)
        => MeasureWidth(text.AsSpan(0, Math.Clamp(count, 0, text.Length)), font);

    /// <summary>Width of a stretch of text. Takes a span because wrapping binary-searches for the break,
    /// and a substring per probe would churn the heap on every frame.</summary>
    private int MeasureWidth(ReadOnlySpan<char> text, Font font)
        => text.IsEmpty ? 0 : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, _rowHeight),
               TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

    /// <summary>Character selection is a plain-text idea, so it is not offered while the line is split into
    /// columns - a click there keeps meaning "select this row".</summary>
    private bool CharSelectionAvailable => _doc is not null && !_doc.Columns.Enabled;

    // ---- find highlighting ----

    private FindEngine.FindMatcher? _highlight;
    private readonly List<(int At, int Length, Color Colour)> _highlights = new();

    /// <summary>Marks every occurrence of a term on the lines currently on screen. Set while a find term is
    /// live - which outlasts the dialog, since F3 does - and cleared when it is dropped.</summary>
    public void SetFindHighlight(FindEngine.FindMatcher? matcher)
    {
        _highlight = matcher;
        Invalidate();
    }

    /// <summary>Every occurrence to mark on a line: the find term, and what is selected elsewhere. The line
    /// the caret is on gets the stronger colour, so which line the search landed on is obvious without the
    /// navigation having to work in occurrences.</summary>
    private void CollectHighlights(string text, long row)
    {
        _highlights.Clear();
        var matcher = _highlight;
        string? selected = SelectedText;
        if (matcher is null && selected is null) return;

        Color colour = row == _caretRow ? _settings.FindCurrent : _settings.FindHighlight;
        if (matcher is not null)
        {
            int from = 0;
            while (matcher.NextMatch(text, from, out int at, out int len))
            {
                _highlights.Add((at, len, colour));
                from = at + Math.Max(1, len);
            }
        }
        // Occurrences of what is selected, so picking a request id out of one line shows the rest at once.
        if (selected is { Length: > 1 })
        {
            int from = 0;
            while (from < text.Length)
            {
                int at = text.AsSpan(from).IndexOf(selected, StringComparison.Ordinal);
                if (at < 0) break;
                if (row != _charRow || from + at != Math.Min(_charAnchor, _charFocus))
                    _highlights.Add((from + at, selected.Length, _settings.FindHighlight));
                from += at + selected.Length;
            }
        }
    }

    private void FillHighlights(Graphics g, string text, int from, int to, int gutter, int y, Font font)
    {
        foreach (var (at, len, colour) in _highlights)
        {
            int a = Math.Max(at, from), b = Math.Min(at + len, to);
            if (b <= a) continue;
            int x0 = SegmentX(text, from, a, gutter, font);
            int x1 = SegmentX(text, from, b, gutter, font);
            using var brush = new SolidBrush(colour);
            g.FillRectangle(brush, x0, y, Math.Max(1, x1 - x0), _rowHeight);
        }
    }

    /// <summary>Re-draws the marked text over its own fill in the ordinary text colour. Without this a hit on
    /// a selected row would be white on orange - and the row the search just landed on is always selected.</summary>
    private void DrawHighlightText(Graphics g, string text, int from, int to, int gutter, int y, Font font)
    {
        foreach (var (at, len, _) in _highlights)
        {
            int a = Math.Max(at, from), b = Math.Min(at + len, to);
            if (b <= a) continue;
            int x0 = SegmentX(text, from, a, gutter, font);
            TextRenderer.DrawText(g, text[a..b], font, new Point(x0, y), _settings.Foreground, TextFlags);
        }
    }

    // ---- what the match map reads ----

    internal CascadeDocument? Document => _doc;
    internal AppSettings Settings => _settings;
    internal long FirstVisibleRow => _firstRow;
    internal int VisibleRows => EffectiveVisibleRows;
    internal long CaretRow => _caretRow;

    /// <summary>What the minimap needs to draw the selection, and to know when it has moved.</summary>
    internal bool HasSelection => _sel.Count > 0;
    internal bool IsRowSelected(long row) => _sel.Contains(row);
    internal bool SelectionIntersects(long from, long toExclusive) => _sel.IntersectsRange(from, toExclusive);
    internal long SelectionVersion => _sel.Version * 1_000_003L + _caretRow;

    /// <summary>Scrolls so <paramref name="row"/> is the top visible row. Used by the map, which stands in
    /// for the scrollbar, so it drops the view anchor exactly as dragging the thumb does.</summary>
    internal void ScrollToRow(long row)
    {
        ClearViewAnchor();
        SetFirstRow(row);
        Invalidate();
    }

    /// <summary>A wheel turn over the map scrolls the text, as it does over the scrollbar.</summary>
    internal void ScrollByWheel(int delta) => ScrollBy(-Math.Sign(delta) * SystemInformation.MouseWheelScrollLines);

    /// <summary>Shows or hides the minimap. The scrollbar stays either way: it is the one that covers the
    /// whole file, and the map only ever shows a window of it.</summary>
    internal void SetMatchMapVisible(bool visible)
    {
        if (_map is null) return;
        _map.Visible = visible;
        RefreshView();
    }

    /// <summary>Tells the map its summary is stale (the filters, markers or file changed).</summary>
    internal void InvalidateMatchMap() => _map?.InvalidateSummary();

    internal MiniMapControl? MatchMapForTesting => _map;

    internal SlimScrollBar ScrollBarForTesting => _vbar;
    internal SlimScrollBar HScrollBarForTesting => _hbar;

    internal int MapWidthForTesting => _map?.Visible == true ? _map.Width : 0;

    internal int ScrollBarWidthForTesting => _vbar.Visible ? _vbar.Width : 0;

    /// <summary>Where the two actually are, so a test never has to work it out from docking order.</summary>
    internal Rectangle MapBoundsForTesting => _map?.Visible == true ? _map.Bounds : Rectangle.Empty;

    internal Rectangle ScrollBarBoundsForTesting => _vbar.Visible ? _vbar.Bounds : Rectangle.Empty;

    internal bool VerticalScrollBarVisibleForTesting => _vbar.Visible;

    /// <summary>Width taken by the map and the scrollbar together.</summary>
    private int RightGutterWidth => (_map?.Visible == true ? _map.Width : 0) + (_vbar.Visible ? _vbar.Width : 0);

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
        bool caretOnScreen = _caretRow >= top && _caretRow < Math.Min(rows, top + EffectiveVisibleRows);
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
        _anchorOffset = Math.Clamp(anchor.Offset, 0, Math.Max(0, EffectiveVisibleRows - 1));
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
        if (rows <= 0) return 0;
        long limit = Math.Max(0, rows - EffectiveVisibleRows);
        // While wrapping, the count from the last frame is only a hint - the last screenful may hold a very
        // different number of rows. Work the real limit out when it is about to matter, or the view either
        // stops short of the end or runs off it into empty space.
        if (Wrapping && first > limit) limit = FirstRowShowing(rows - 1, fill: true);
        return Math.Clamp(first, 0, Math.Max(0, limit));
    }

    /// <summary>Scrolls so <paramref name="first"/> is the top visible row (clamped), syncing the scrollbar.</summary>
    private void SetFirstRow(long first)
    {
        _firstRow = ClampFirstRow(first);
        SyncVScrollValue();
    }

    /// <summary>Pushes <see cref="_firstRow"/> into the scrollbar. It clamps to its own range, which keeps
    /// growing as rows stream in - harmless here because setting Value reports nothing back, unlike a
    /// gesture on the bar.</summary>
    private void SyncVScrollValue() => _vbar.Value = _firstRow;

    public void Attach(CascadeDocument doc, AppSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _firstRow = 0;
        _hScroll = 0;
        _caretRow = -1;
        _sel.Clear();
        ClearCharSelection();
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
        // After the fonts made from it, never before: a font keeps its family alive behind it.
        _fontFamily?.Dispose();
        float size = _settings.EffectiveFontSize;
        FontFamily family;
        try { family = new FontFamily(_settings.FontFamily); }
        catch { family = FontFamily.GenericMonospace; }
        _fontFamily = ReferenceEquals(family, FontFamily.GenericMonospace) ? null : family;
        _fontRegular = new Font(family, size, FontStyle.Regular);
        _fontBold = new Font(family, size, FontStyle.Bold);
        _fontItalic = new Font(family, size, FontStyle.Italic);
        _fontBoldItalic = new Font(family, size, FontStyle.Bold | FontStyle.Italic);
        // Font.Height is the typeface's own line spacing, which for a monospaced face already includes
        // whatever gap its designer wanted between lines - so anything added here is the reader's choice,
        // not a correction. Two pixels used to be added unasked, costing a line in every eleven on screen.
        _rowHeight = Math.Max(_fontRegular.Height + Math.Max(0, _settings.ExtraLineSpacing), 8);
        _charWidth = Math.Max(1, TextRenderer.MeasureText("0", _fontRegular, new Size(1000, 100),
            TextFormatFlags.NoPadding).Width);
        Invalidate();
    }

    /// <summary>Recomputes scrollbar ranges from the document and repaints. Call (on the UI thread)
    /// whenever counts change or the view mode/filters change.</summary>
    public void RefreshView()
    {
        long rows = _doc?.RowCount ?? 0;
        int visible = EffectiveVisibleRows;

        _firstRow = ClampFirstRow(_firstRow);
        if (_caretRow >= rows) _caretRow = rows - 1;

        int vMax = (int)Math.Min(int.MaxValue, Math.Max(0, rows - 1));
        _vbar.Configure(rows, visible);

        UpdateHScroll();
        AnchorToViewportIfStreaming();
        if (_anchorLine >= 0) ApplyViewAnchor();
        SyncVScrollValue(); // reflect the final position in the (possibly grown) range
        Invalidate();
    }

    private void UpdateHScroll()
    {
        // Nothing runs off the side while wrapping, so the scrollbar has nothing to say.
        bool wasShowing = _hbar.Visible;
        _hbar.Visible = !Wrapping;
        if (_hbar.Visible != wasShowing) ChromeChanged?.Invoke();
        if (Wrapping) { _hScroll = 0; return; }
        // The paint keeps the widest line up to date as it draws, but the range has to be right before the
        // first paint too - a window that has not painted reports nothing to scroll, and then Home and End
        // have nowhere to go.
        if (_maxContentWidth <= 0) _maxContentWidth = MeasureVisibleWidth();
        int viewport = Math.Max(1, ContentWidth);
        int max = Math.Max(_maxContentWidth, viewport);
        _hbar.Configure(max, viewport);
        if (_hScroll > max - viewport) _hScroll = Math.Max(0, max - viewport);
        _hbar.Value = _hScroll;
    }

    /// <summary>The widest of the rows on screen. The same measurement the paint makes, so the two agree.</summary>
    private int MeasureVisibleWidth()
    {
        if (_doc is null || Wrapping) return 0;
        long rows = _doc.RowCount;
        int widest = 0;
        for (int i = 0; i < VisibleRowCount; i++)
        {
            long row = _firstRow + i;
            if (row >= rows) break;
            string text = Expand(_doc.GetLineText(_doc.RowToLine(row)));
            widest = Math.Max(widest, TextRenderer.MeasureText(text, _fontRegular,
                new Size(int.MaxValue, _rowHeight),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 8);
        }
        return widest;
    }

    private int ContentWidth => Math.Max(0, ClientSize.Width - RightGutterWidth - GutterWidth());
    /// <summary>Puts a bar above the text, INSIDE this view - so it stops short of the map and the
    /// scrollbar rather than pushing them down, which is what docking it above the whole view did. It is
    /// placed at the front of the child list because docking runs from the back of that list forwards, so
    /// the front-most is laid out last and gets what the full-height strips have left.</summary>
    internal void HostAtTop(Control bar)
    {
        _topBar = bar;
        bar.Dock = DockStyle.Top;
        Controls.Add(bar);
        Controls.SetChildIndex(bar, 0);
        bar.VisibleChanged += (_, _) => { ChromeChanged?.Invoke(); RefreshView(); };
        bar.SizeChanged += (_, _) => { ChromeChanged?.Invoke(); RefreshView(); };
    }

    private Control? _topBar;

    /// <summary>Room taken above the text by a hosted bar.</summary>
    private int TopInset => _topBar is { Visible: true } ? _topBar.Height : 0;

    /// <summary>Where the text starts: below anything sitting above it and below the column header.</summary>
    private int TextTop => TopInset + HeaderHeight;

    private int HeaderHeight => (_doc?.Columns.Enabled ?? false) ? _rowHeight : 0;

    /// <summary>Room the sideways scrollbar takes at the bottom. Nothing at all when it is hidden - a
    /// hidden docked control gives its space back, so counting its height anyway left a strip of the view
    /// unused, and since that strip is about a line tall it read as a line failing to draw.</summary>
    private int BottomInset => _hbar.Visible ? _hbar.Height : 0;

    private int VisibleRowCount => Math.Max(1, (ClientSize.Height - BottomInset - TextTop) / Math.Max(1, _rowHeight));

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

    /// <summary>Whether lines are broken to fit the width. Not offered while they are split into columns:
    /// wrapping inside a cell is a different feature, and the menu says so by greying out.</summary>
    internal bool Wrapping => _settings.WordWrap && !(_doc?.Columns.Enabled ?? false);

    /// <summary>How tall one line is drawn, and how much of the control is not text. Between them they say
    /// what heights this control can be given without a strip of dead space at the bottom.</summary>
    internal int RowPitch => Math.Max(1, _rowHeight);
    internal int ChromeHeight => TextTop + BottomInset;

    /// <summary>Raised when <see cref="ChromeHeight"/> changes, which it does whenever the sideways
    /// scrollbar comes or goes - so whoever sized this control can put it back on a whole line.</summary>
    internal event Action? ChromeChanged;

    /// <summary>The most segments one line may take. A single enormous line would otherwise fill the window
    /// on its own, leaving no way to see what surrounds it.</summary>
    private const int MaxWrapSegments = 20;

    /// <summary>Rows on screen. With wrapping this is what the last frame actually fitted, since a row may
    /// be several segments tall; without it, the arithmetic answer.</summary>
    private int EffectiveVisibleRows => Wrapping && _layout.Count > 0 ? _layout.Count : VisibleRowCount;

    /// <summary>Breaks a line into the segments it is drawn as, appending each segment's start index to
    /// <paramref name="starts"/>. Breaks at a space where there is one, and mid-word only when a single word
    /// is wider than the view.</summary>
    private int WrapInto(string text, int width, Font font, List<int> starts)
    {
        starts.Clear();
        starts.Add(0);
        if (!Wrapping || width <= 0 || text.Length == 0) return 1;
        if (PrefixWidth(text, text.Length, font) <= width) return 1;

        int at = 0;
        while (at < text.Length && starts.Count < MaxWrapSegments)
        {
            int lo = 1, hi = text.Length - at;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (MeasureWidth(text.AsSpan(at, mid), font) <= width) lo = mid; else hi = mid - 1;
            }
            int take = Math.Max(1, lo);
            if (at + take < text.Length)
            {
                int space = text.LastIndexOf(' ', at + take - 1, take);
                if (space > at) take = space - at + 1;   // keep the space on the line it ends
            }
            at += take;
            if (at < text.Length) starts.Add(at);
            else break;
        }
        return starts.Count;
    }

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

    internal int VisibleRowCountForTesting => VisibleRowCount;

    internal long CaretRowForTesting => _caretRow;

    internal void PressKeyForTesting(Keys key) => OnKeyDown(new KeyEventArgs(key));

    // ---- test seams for selecting with the mouse ----

    /// <summary>Screen x of a character in a row, so a check can aim at one rather than guess pixels.</summary>
    internal int XForCharForTesting(long row, int index)
        => SegmentX(DisplayText(row), 0, index, GutterWidth(), FontForRow(row, DisplayText(row)));

    private int YForRowForTesting(long row)
    {
        foreach (var (r, top, height, _) in _layout)
            if (r == row) return top + Math.Min(height, _rowHeight) / 2;
        return TextTop + (int)(row - _firstRow) * _rowHeight + _rowHeight / 2;
    }

    /// <summary>How many segments a row was drawn as. 1 unless it wrapped.</summary>
    internal int SegmentsForTesting(long row)
    {
        foreach (var (r, _, _, segments) in _layout)
            if (r == row) return segments;
        return 0;
    }

    internal int RowsPaintedForTesting => _layout.Count;
    internal long CharOriginForTesting => _charOriginRow;
    internal int ViewportHeightForTesting => ViewportHeight;
    internal int RowHeightOfForTesting(long row) => RowHeightOf(row);
    internal Font FontForTesting => _fontRegular;
    internal FontFamily? FontFamilyForTesting => _fontFamily;

    /// <summary>Top of a row as painted, so a check can aim at a wrapped row's second segment.</summary>
    internal int RowTopForTesting(long row)
    {
        foreach (var (r, top, _, _) in _layout)
            if (r == row) return top;
        return TextTop;
    }

    internal int RowHeightForTesting => _rowHeight;

    /// <summary>Clicks at an explicit y, rather than at a row's middle - the point being to land somewhere
    /// a fixed row height would have put a different row.</summary>
    internal void ClickForTesting2(int y, int x)
    {
        ForgetLastClick();
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
    }

    /// <summary>Y of the middle of a row, for a check that looks at what was painted there.</summary>
    internal int RowMiddleForTesting(long row) => YForRowForTesting(row);

    /// <summary>Forgets the last click, so one check's clicks cannot look like a double-click to the next.
    /// A real user gets that behaviour on purpose; a test asking about something else does not.</summary>
    private void ForgetLastClick() { _clickCount = 0; _lastClickAt = DateTime.MinValue; }

    internal void ClickForTesting(long row, int x)
    {
        ForgetLastClick();
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, YForRowForTesting(row), 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, YForRowForTesting(row), 0));
    }

    /// <summary>Presses without letting go, so a drag can be followed step by step.</summary>
    internal void PressForTesting(long row, int x)
    {
        ForgetLastClick();
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, YForRowForTesting(row), 0));
    }

    internal void DoubleClickForTesting(long row, int x)
    {
        ClickForTesting(row, x);
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 2, x, YForRowForTesting(row), 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 2, x, YForRowForTesting(row), 0));
    }

    internal void DragForTesting(long row, int fromX, int toX)
    {
        ForgetLastClick();
        int y = YForRowForTesting(row);
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, fromX, y, 0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, toX, y, 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, toX, y, 0));
    }

    /// <summary>Continues the drag started by the last press onto another row.</summary>
    internal void DragToRowForTesting(long row, int x)
    {
        int y = YForRowForTesting(row);
        _dragging = true;
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x, y, 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
    }

    /// <summary>Moves the pointer mid-drag without letting go, so a wandering drag can be followed.</summary>
    internal void DragOverRowForTesting(long row, int x)
    {
        _dragging = true;
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x, YForRowForTesting(row), 0));
    }

    internal void ReleaseForTesting(long row, int x)
        => OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, YForRowForTesting(row), 0));

    /// <summary>Drives the vertical scrollbar the way UI Automation does: by setting Value, which raises
    /// ValueChanged but never Scroll.</summary>
    internal void SetVerticalScrollValue(int firstRow) { ClearViewAnchor(); SetFirstRow(firstRow); Invalidate(); }

    /// <summary>
    /// The margin that horizontal scrolling must leave pixel-identical: the marker and line-number columns,
    /// excluding the column header and the horizontal scrollbar - that scrollbar's thumb sits under the
    /// margin and does of course move when you scroll.
    /// </summary>
    internal Rectangle GutterAreaForTesting =>
        new(0, TextTop, GutterWidth(), Math.Max(0, ClientSize.Height - TextTop - BottomInset));

    /// <summary>Scrolls the view horizontally, as dragging the horizontal scrollbar does.</summary>
    internal void ScrollHorizontallyTo(int x) => SetHScroll(x);

    /// <summary>The furthest right the view can go: the scrollbar's own limit, so it can never be driven
    /// past the longest line currently measured.</summary>
    private int MaxHScroll => (int)_hbar.MaxValue;

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
        // One more than the arithmetic answer while wrapping: the last row on screen may begin inside the
        // view and run past the bottom, and a budget of whole rows would stop short of drawing it.
        int visible = VisibleRowCount + (Wrapping ? 1 : 0);

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

        _layout.Clear();
        int atY = TextTop;
        int bottom = ClientSize.Height - BottomInset;
        for (int i = 0; i < visible; i++)
        {
            long row = _firstRow + i;
            if (row >= rows || i >= windowCount) break;
            if (atY >= bottom) break;
            int y = atY;
            long line = _window[i];
            string text = _doc.GetLineText(line);
            var eval = _doc.EvaluateText(text, line);

            ResolvedStyle style = eval.ColorFilter is not null
                ? StyleResolver.Resolve(eval.ColorFilter, defaults)
                : defaults;

            bool charSel = HasCharSelection && row == _charRow;
            bool selected = !charSel && _sel.Contains(row);
            bool dim = !_doc.FilteredMode && !eval.Shown;

            Color back = selected ? _settings.SelectionBack : ToColor(style.Background);
            Color fore = selected ? _settings.SelectionFore : (dim ? _settings.DimForeground : ToColor(style.Foreground));

            Font font = SelectFont(style);
            string shown = columns ? text : Expand(text);
            int segments = columns ? 1 : WrapInto(shown, contentW, font, _segments);
            int rowHeight = segments * _rowHeight;
            _layout.Add((row, y, rowHeight, segments));

            var rowRect = new Rectangle(0, y, ClientSize.Width - RightGutterWidth, rowHeight);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, rowRect);

            DrawMarkers(g, line, y, rowHeight);
            DrawLineNumber(g, line, y, rowHeight, selected);

            var contentRect = new Rectangle(gutter, y, contentW, rowHeight);
            var clip = g.Clip;
            g.SetClip(contentRect);
            if (columns && splitter is not null)
                runningMaxWidth = Math.Max(runningMaxWidth, DrawColumns(g, splitter, text, gutter, y, fore, font));
            else
            {
                CollectHighlights(shown, row);
                for (int s = 0; s < segments; s++)
                {
                    int from = _segments[s];
                    int to = s + 1 < _segments.Count ? _segments[s + 1] : shown.Length;
                    int sy = y + s * _rowHeight;
                    FillHighlights(g, shown, from, to, gutter, sy, font);
                    runningMaxWidth = Math.Max(runningMaxWidth, DrawSegment(g, shown, from, to, gutter, sy, fore, font));
                    DrawHighlightText(g, shown, from, to, gutter, sy, font);
                    if (charSel) DrawCharSelection(g, shown, from, to, gutter, sy, font);
                }
            }
            g.Clip = clip;

            if (_doc.IsLineTruncated(line))
                TextRenderer.DrawText(g, " […]", _fontItalic,
                    new Point(ClientSize.Width - RightGutterWidth - 40, y), Color.Gray);

            if (row == _caretRow && Focused)
                using (var pen = new Pen(Color.FromArgb(120, _settings.SelectionBack))) g.DrawRectangle(pen, 0, y, rowRect.Width - 1, rowHeight - 1);

            atY += rowHeight;
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
        int top = TopInset;
        var rect = new Rectangle(0, top, ClientSize.Width - RightGutterWidth, _rowHeight);
        using (var b = new SolidBrush(_settings.GutterBack)) g.FillRectangle(b, rect);
        using (var pen = new Pen(Color.FromArgb(210, 210, 210))) g.DrawLine(pen, 0, rect.Bottom - 1, rect.Width, rect.Bottom - 1);
        int x = gutter - _hScroll;
        var clip = g.Clip;
        g.SetClip(new Rectangle(gutter, top, contentW, _rowHeight));
        foreach (var def in _doc!.Columns.Columns)
        {
            if (!def.Visible) continue;
            int w = def.Width > 0 ? def.Width : DefaultColumnWidth;
            TextRenderer.DrawText(g, def.Name, _fontBold, new Rectangle(x + 3, top + 1, w - 6, _rowHeight - 2),
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
        text = Expand(text);
        var pt = new Point(gutter - _hScroll, y);
        TextRenderer.DrawText(g, text, font, pt, fore, TextFlags);
        int w = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, _rowHeight), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        return w + 8;
    }

    /// <summary>Draws one segment of a line - the whole of it when nothing is wrapped. Returns how wide the
    /// content is, which is what the horizontal scrollbar's range is built from.</summary>
    private int DrawSegment(Graphics g, string text, int from, int to, int gutter, int y, Color fore, Font font)
    {
        string part = text[from..to];
        int x = gutter - (Wrapping ? 0 : _hScroll);
        TextRenderer.DrawText(g, part, font, new Point(x, y), fore, TextFlags);
        if (Wrapping) return 0;   // nothing scrolls sideways while wrapping, so nothing to measure against
        int w = TextRenderer.MeasureText(g, part, font, new Size(int.MaxValue, _rowHeight),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        return w + 8;
    }

    /// <summary>Paints the selected part of a line over the text already drawn, clipped to one segment. The
    /// row keeps its own colours - only the range is in the selection colours - which is how a text box
    /// reads.</summary>
    private void DrawCharSelection(Graphics g, string text, int from, int to, int gutter, int y, Font font)
    {
        int a = Math.Clamp(Math.Min(_charAnchor, _charFocus), 0, text.Length);
        int b = Math.Clamp(Math.Max(_charAnchor, _charFocus), 0, text.Length);
        a = Math.Max(a, from);
        b = Math.Min(b, to);
        if (b <= a) return;
        int x0 = SegmentX(text, from, a, gutter, font);
        int x1 = SegmentX(text, from, b, gutter, font);
        using (var brush = new SolidBrush(_settings.SelectionBack)) g.FillRectangle(brush, x0, y, Math.Max(1, x1 - x0), _rowHeight);
        TextRenderer.DrawText(g, text[a..b], font, new Point(x0, y), _settings.SelectionFore, TextFlags);
    }

    /// <summary>Where a character sits on screen, measured from the start of the segment it is drawn in.</summary>
    private int SegmentX(string text, int segmentStart, int index, int gutter, Font font)
        => gutter - (Wrapping ? 0 : _hScroll) + PrefixWidth(text[segmentStart..], index - segmentStart, font);

    private void DrawMarkers(Graphics g, long line, int y, int height)
    {
        if (!MarkersVisible || _doc is null) return;
        // Keep the marker gutter the neutral margin color (not the line's fill color) so the marker
        // bars stay clearly visible regardless of the line's filter highlight or selection.
        using (var bg = new SolidBrush(_settings.GutterBack))
            g.FillRectangle(bg, 0, y, MarkerGutterWidth, height);
        byte mask = _doc.Markers.MaskOf(line);
        if (mask == 0) return;
        for (int m = 0; m < 8; m++)
        {
            if ((mask & (1 << m)) == 0) continue;
            using var b = new SolidBrush(AppSettings.MarkerColors[m]);
            g.FillRectangle(b, 3 + m * 5, y + 2, 4, _rowHeight - 4);
        }
    }

    private void DrawLineNumber(Graphics g, long line, int y, int height, bool selected)
    {
        int lnw = LineNumberGutterWidth;
        if (lnw == 0) return;
        int x = MarkerGutterWidth;
        // The whole height of the row, not one line of it: a wrapped row is several lines tall, and the
        // segments below the first would otherwise keep the row's own fill - or its selection colour.
        using (var b = new SolidBrush(_settings.GutterBack)) g.FillRectangle(b, x, y, lnw, height);
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

        // Windows only ever reports one or two clicks, so a triple has to be counted here.
        bool repeat = (DateTime.UtcNow - _lastClickAt).TotalMilliseconds <= SystemInformation.DoubleClickTime
                      && Math.Abs(e.X - _lastClickAtPoint.X) <= SystemInformation.DragSize.Width
                      && Math.Abs(e.Y - _lastClickAtPoint.Y) <= SystemInformation.DragSize.Height;
        _clickCount = repeat ? Math.Min(3, _clickCount + 1) : 1;
        _lastClickAt = DateTime.UtcNow;
        _lastClickAtPoint = e.Location;

        if (_clickCount == 2)
        {
            // Double-click writes a filter for this line - the part picked out if there was one, the whole
            // line otherwise. It does NOT select the word under the pointer: this view is for reading a log,
            // and turning a line into a filter is the thing worth a gesture that short.
            NewFilterRequested?.Invoke(_carriedSelection);
            return;
        }

        // Remembered before the click below throws it away, so the double-click that may follow can still
        // make a filter from the part of the line that was picked out.
        _carriedSelection = row == _charRow && HasCharSelection ? SelectedText : null;

        // A plain click - and a triple click - means the whole line, which is also where a drag starts from.
        ClearCharSelection();
        if ((ModifierKeys & Keys.Shift) != 0 && _sel.Anchor >= 0) _sel.SetRange(_sel.Anchor, row);
        else if ((ModifierKeys & Keys.Control) != 0) _sel.ToggleSingle(row);
        else _sel.SetSingle(row);

        _caretRow = row;
        _dragging = true;
        _charDragging = false;
        _charOriginRow = -1;
        if (CharSelectionAvailable && (ModifierKeys & (Keys.Shift | Keys.Control)) == 0)
        {
            // Armed, not shown: a drag that stays on this row turns into a character selection, one that
            // leaves it selects whole rows, and one that comes back picks the characters up again.
            _charRow = _charOriginRow = row;
            _charAnchor = _charFocus = _charOriginAt = CharIndexAt(row, e.X, e.Y);
        }
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        TrackHover(e.Location);
        if (_dragging && _doc is not null)
        {
            long row = Math.Clamp(RowAtY(e.Y), 0, Math.Max(0, _doc.RowCount - 1));
            if (row == _charOriginRow)
            {
                // Back on the row it started from, so it is a selection within that line again.
                _charRow = _charOriginRow;
                _charAnchor = _charOriginAt;
                int at = CharIndexAt(_charRow, e.X, e.Y);
                if (at != _charFocus || _caretRow != row || _sel.Count != 1)
                {
                    _charFocus = at;
                    _charDragging = true;
                    _caretRow = row;
                    _sel.SetSingle(row);
                    Invalidate();
                    Update();
                    SelectionChanged?.Invoke();
                }
            }
            else if (row != _caretRow || _charRow >= 0)
            {
                // Left the row: this is a selection of whole lines after all.
                ClearCharSelection();
                _sel.SetRange(_sel.Anchor, row);
                _caretRow = row;
                EnsureVisible(row);
                Invalidate();
                Update();
                SelectionChanged?.Invoke();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        _charOriginRow = -1;
        // A press that never moved sideways selected the whole line, so drop the empty range it armed.
        if (!_charDragging && !HasCharSelection) ClearCharSelection();
        _charDragging = false;
        base.OnMouseUp(e);
    }

    protected override void Dispose(bool disposing)
    {
        // None of these is a child control, so nothing else would clean them up.
        if (disposing)
        {
            _tipTimer.Dispose(); _tips.Dispose();
            _fontRegular?.Dispose(); _fontBold?.Dispose(); _fontItalic?.Dispose(); _fontBoldItalic?.Dispose();
            _fontFamily?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        HideTip();
        base.OnMouseLeave(e);
    }

    /// <summary>Restarts the hover countdown whenever the pointer moves to a different row, so the tip
    /// describes where the pointer settled rather than where it passed through.</summary>
    private void TrackHover(Point at)
    {
        if (_doc is null || !_settings.ShowFilterTooltips || _dragging) { HideTip(); return; }
        long row = RowAtY(at.Y);
        if (at.Y < TextTop || row < 0 || row >= _doc.RowCount) { HideTip(); return; }
        if (row == _tipRow) return;

        HideTip();
        _tipRow = row;
        _tipPoint = at;
        _tipTimer.Stop();
        _tipTimer.Start();
    }

    private void HideTip()
    {
        _tipTimer.Stop();
        if (_tipRow >= 0) _tips.Hide(this);
        _tipRow = -1;
    }

    private void ShowTipNow()
    {
        _tipTimer.Stop();
        if (_doc is null || _tipRow < 0 || _tipRow >= _doc.RowCount) return;

        string text = FilterTipText.Build(_doc.FiltersMatching(_doc.RowToLine(_tipRow)));
        if (text.Length == 0) return;
        _tips.Show(text, this, _tipPoint.X + 16, _tipPoint.Y + 20, TipDurationMs);
    }

    /// <summary>Builds the tip a hover would show, without the wait or the window.</summary>
    internal string TipTextForTesting(long row)
        => _doc is null ? "" : FilterTipText.Build(_doc.FiltersMatching(_doc.RowToLine(row)));

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            Zoom(e.Delta > 0 ? 10 : -10);
            return;
        }
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            SetHScroll(_hScroll - Math.Sign(e.Delta) * _charWidth * 6);
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
        int page = EffectiveVisibleRows - 1;

        switch (e.KeyCode)
        {
            // Ctrl+Up/Down scrolls the view only - the caret and selection stay where they are, so you can
            // look around without losing your place. Must come before the plain Up/Down cases.
            case Keys.Up when e.Control && !e.Shift && !e.Alt: ScrollBy(-1); break;
            case Keys.Down when e.Control && !e.Shift && !e.Alt: ScrollBy(1); break;
            case Keys.Up: MoveCaret(-1, e.Shift); break;
            case Keys.Down: MoveCaret(1, e.Shift); break;
            case Keys.PageUp: PageCaret(-1, e.Shift); break;
            case Keys.PageDown: PageCaret(1, e.Shift); break;
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
        InvalidateMatchMap();   // the map draws the marks down its edge, so it has to be told
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
        RevealRow(row);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private void MoveCaret(long delta, bool extend)
    {
        SyncFirstRowToAnchor(); // work from the current position, not one a streaming pass has already shifted
        MoveCaretTo((_caretRow < 0 ? _firstRow : _caretRow) + delta, extend);
    }

    /// <summary>Moves the caret a screenful, and leaves it on the row that ends up against the edge it moved
    /// towards. While wrapping, how many rows a screenful is has to be measured where the caret is going
    /// rather than counted from where it came: a screenful of tall wrapped rows is fewer rows than a
    /// screenful of short ones, so counting left the caret short of the bottom.</summary>
    private void PageCaret(int direction, bool extend)
    {
        MoveCaret(direction * Math.Max(1, EffectiveVisibleRows - 1), extend);
        if (!Wrapping || direction < 0 || _doc is null) return;
        long last = Math.Min(_doc.RowCount - 1, _firstRow + Math.Max(1, RowsFittingFrom(_firstRow)) - 1);
        if (last > _caretRow) MoveCaretTo(last, extend, reveal: false);
    }

    private void MoveCaretTo(long row, bool extend, bool reveal = true)
    {
        if (_doc is null) return;
        ClearCharSelection();
        _anchorLine = -1;
        row = Math.Clamp(row, 0, Math.Max(0, _doc.RowCount - 1));
        _caretRow = row;
        if (extend && _sel.Anchor >= 0) _sel.SetRange(_sel.Anchor, row);
        else _sel.SetSingle(row);
        if (reveal) EnsureVisible(row);
        Invalidate();
        Update();   // a held arrow key keeps the queue full, and a repaint waits for it to empty
        SelectionChanged?.Invoke();
    }

    private long RowAtY(int y)
    {
        if (y < TextTop) return -1;
        // A running pass shifts every row index between paints, so re-derive the top row from its anchored
        // line first: otherwise a click maps to whatever was under the cursor a frame (thousands of rows) ago.
        SyncFirstRowToAnchor();
        // With wrapping a row is as tall as it needs to be, so where each one landed is read from the last
        // frame rather than divided out of a fixed height.
        if (Wrapping && _layout.Count > 0)
        {
            foreach (var (row, top, height, _) in _layout)
                if (y >= top && y < top + height) return row;
            return _layout[^1].Row + 1;   // below the last painted row
        }
        return _firstRow + (y - TextTop) / _rowHeight;
    }

    /// <summary>Runs a change that takes rows off the top of the view (or hands them back) and scrolls by as
    /// much, so every line still showing keeps its place on screen - the alternative is the whole log
    /// appearing to slide down and lose its last lines rather than simply having its top ones covered.
    /// The row to settle on is worked out BEFORE the change and applied after: at the end of the file the
    /// resize already moves the view on its own, and a relative scroll would then count that twice.</summary>
    internal void KeepTextStillAcross(int rowsTakenFromTop, Action change)
    {
        SyncFirstRowToAnchor();
        long want = _firstRow + rowsTakenFromTop;
        change();
        // Dropped AFTER the change, not before: laying out again re-arms the streaming anchor at the row the
        // view was showing, and while the file is still being read that would pull it straight back. The
        // next paint re-arms it at wherever this leaves the view.
        ClearViewAnchor();
        SetFirstRow(want);
        Invalidate();
        Update();
    }

    private void ScrollBy(int deltaRows)
    {
        // Scroll relative to where the view actually is now, not to a row index captured a frame ago (which a
        // running pass may already have shifted by thousands of rows).
        SyncFirstRowToAnchor();
        ClearViewAnchor();
        SetFirstRow(_firstRow + deltaRows);
        Invalidate();
        Update();   // held Ctrl+arrow, or a wheel spun hard, would otherwise not draw until it stopped
    }

    /// <summary>Scrolls a row into view by the shortest move that gets it there.
    ///
    /// Counted in rows the view is actually showing, which is not the arithmetic answer while wrapping: a
    /// row can be several segments tall, so far fewer of them fit. Using the flat count there left the
    /// caret believed to be on screen when a page down had carried it well past the bottom.</summary>
    private void EnsureVisible(long row)
    {
        if (row < _firstRow) { SetFirstRow(row); return; }
        if (row < _firstRow + EffectiveVisibleRows) return;
        SetFirstRow(FirstRowShowing(row));
    }

    /// <summary>Which row has to be at the top for <paramref name="last"/> to be the bottom one on screen.
    /// While wrapping this is measured rather than counted back: rows differ in height, so the number that
    /// fitted at one place in the file says nothing about how many fit at another.
    ///
    /// <paramref name="fill"/> asks a different question - how far the view may scroll - and settles for
    /// <paramref name="last"/> merely starting on screen, so it takes every row above it that still leaves
    /// room for it to begin. Requiring the whole of it to fit instead leaves the bottom of the view blank by
    /// however much the row above would have overhung.</summary>
    private long FirstRowShowing(long last, bool fill = false)
    {
        if (_doc is null || !Wrapping) return last - VisibleRowCount + 1;
        long room = ViewportHeight;
        long used = fill ? 0 : RowHeightOf(last);
        if (!fill && used > room) return last;
        long row = last - 1;
        while (row >= 0)
        {
            long height = RowHeightOf(row);
            if (fill ? used + height >= room : used + height > room) break;
            used += height;
            row--;
        }
        return Math.Clamp(row + 1, 0, last);
    }

    /// <summary>How many rows fit starting at <paramref name="first"/>, counted the way the paint counts
    /// them: a row whose top is on screen is drawn, even where its last segment runs past the bottom.</summary>
    private int RowsFittingFrom(long first)
    {
        if (_doc is null || !Wrapping) return VisibleRowCount;
        long rows = _doc.RowCount, room = ViewportHeight, used = 0, row = first;
        int fitted = 0;
        while (row < rows && used < room)
        {
            used += RowHeightOf(row);
            fitted++;
            row++;
        }
        return Math.Max(1, fitted);
    }

    private int ViewportHeight => Math.Max(1, ClientSize.Height - BottomInset - TextTop);

    /// <summary>How tall a row is drawn, measured the way the paint measures it so the two agree.</summary>
    private int RowHeightOf(long row)
    {
        if (_doc is null) return _rowHeight;
        long line = _doc.RowToLine(row);
        string text = _doc.GetLineText(line);
        var eval = _doc.EvaluateText(text, line);
        var defaults = new ResolvedStyle(ToRgb(_settings.Foreground), ToRgb(_settings.Background), false, false);
        var font = SelectFont(eval.ColorFilter is not null ? StyleResolver.Resolve(eval.ColorFilter, defaults) : defaults);
        return WrapInto(Expand(text), ContentWidth, font, _segments) * _rowHeight;
    }

    /// <summary>Scrolls a jumped-to row into the middle half of the view, so it arrives with context on
    /// both sides instead of hard against an edge. Coming from below it settles at the bottom of that band
    /// and from above at the top, and a row already inside it does not move at all - so walking through
    /// nearby matches does not drag the view about.</summary>
    private void RevealRow(long row)
    {
        int visible = EffectiveVisibleRows;
        long top = visible / 4;
        long bottom = Math.Max(top, visible * 3 / 4 - 1);   // Max guards a viewport only a row or two tall
        if (row < _firstRow + top) SetFirstRow(row - top);
        else if (row > _firstRow + bottom) SetFirstRow(row - bottom);
    }

    public void SelectAll() { if (_doc is not null) { ClearCharSelection(); _sel.SelectAll(_doc.RowCount); Invalidate(); SelectionChanged?.Invoke(); } }

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
        ClearCharSelection();
        _anchorLine = -1;
        long row = _doc.RowForLine(line);
        if (row < 0) row = _doc.RowAtOrAfterLine(line);
        row = Math.Clamp(row, 0, Math.Max(0, _doc.RowCount - 1));
        _caretRow = row;
        _sel.SetSingle(row);
        RevealRow(row);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    public void CopySelection(bool withLineNumbers)
    {
        if (_doc is null) return;
        if (SelectedText is { } part)
        {
            string one = withLineNumbers ? $"{_doc.RowToLine(_charRow) + 1}\t{part}" : part;
            try { Clipboard.SetText(one); } catch { /* clipboard busy */ }
            return;
        }
        if (_sel.Count == 0) return;
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
        return (int)Math.Max(0, Math.Min(EffectiveVisibleRows, rows - _firstRow));
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
            int i = (client.Y - _g.TextTop) / Math.Max(1, _g._rowHeight);
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
                // Read where the row was actually painted: with word wrap a row is as tall as the number of
                // segments it broke into, so its place cannot be multiplied out of a fixed height.
                int w = Math.Max(0, _g.ClientSize.Width - _g.RightGutterWidth);
                if (_visibleIndex < _g._layout.Count)
                {
                    var (_, top, height, _) = _g._layout[_visibleIndex];
                    return _g.RectangleToScreen(new Rectangle(0, top, w, height));
                }
                int y = _g.TextTop + _visibleIndex * _g._rowHeight;
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

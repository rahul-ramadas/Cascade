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
    private readonly LineSelection _sel = new();
    private readonly List<ColumnValue> _cols = new();

    // ---- column geometry and the gestures on the header (see the "columns" region below) ----
    private int[] _colWidths = [];      // resolved pixel width per column in the spec; 0 when hidden
    private int[] _colNatural = [];     // what each column's content asked for when it was last measured
    private string? _naturalKey;        // what that measurement was made of, so it is not repeated per scroll
    private ColumnGesture _colGesture;
    private int _colIndex = -1;         // the column being resized or reordered
    private int _colGrabX, _colGrabWidth;
    private bool _colMoved;
    private TextBox? _renameBox;
    private int _renameIndex = -1;
    private bool _renaming;

    private CascadeDocument? _doc;
    private AppSettings _settings = new();

    // One font per combination of bold, italic and underline, indexed by those three bits. An array
    // rather than a field each: three flags is eight faces.
    private readonly Font[] _fonts = new Font[8];
    private Font FontRegular => _fonts[0];
    private Font FontBold => _fonts[1];
    private Font FontItalic => _fonts[2];
    private FontFamily? _fontFamily;
    private int _rowHeight = 16;
    private int _charWidth = 8;
    private readonly int[] _charWidths = new int[8];
    private readonly Dictionary<int, SolidBrush> _brushes = new();
    /// <summary>Whether every character is the same width, measured rather than asked of the family name.
    /// It is what decides whether a column is sized in characters or in pixels.</summary>
    private bool _monospaced = true;

    private long _firstRow;
    private int _hScroll;
    private int _maxContentWidth;
    private int _paints;
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

    // Character selection within one line. There is no caret and none is drawn - nothing here is editable -
    // so this is purely a highlighted range that any navigation drops. Indices are into the line's DISPLAYED
    // text (tabs already expanded), which is what the hit test and the painting both work in.
    // Held against the FILE LINE, not the row it happens to be on: the row moves whenever the filters do.
    private long _charLine = -1;
    private int _charAnchor, _charFocus;
    private bool _charDragging;
    // Which column the selection lives in while the log is split into cells, so a drag stays inside the
    // cell it began in. -1 means "whole lines", which is also how a selection made in one mode is spotted
    // and dropped when the other is turned on.
    private int _charColumn = -1;
    // Where a drag first took hold. Kept while the drag wanders onto other rows, which is what lets coming
    // back to that row go back to selecting characters on it. A ROW, unlike the selection itself: it lives
    // only for the length of a gesture, and what it answers is "is the pointer back where it started".
    private long _charOriginRow = -1;
    private int _charOriginAt;
    private int _charOriginColumn = -1;
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
        // Opaque as well as user-painted: the paint below covers every pixel that is not a child window -
        // a row, or the strip of background under the last one - so letting WinForms fill the client area
        // first only writes the whole window twice.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
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

    /// <summary>The file line a display row is showing, or -1 when there is no such row. Everything the
    /// reader picked out is remembered by line, so this is where rows are turned into that.</summary>
    private long LineAt(long row)
        => _doc is not null && row >= 0 && row < _doc.RowCount ? _doc.RowToLine(row) : -1;

    // ---- character selection ----

    /// <summary>Whether part of one line is selected, as opposed to whole lines.</summary>
    public bool HasCharSelection => _charLine >= 0 && _charFocus != _charAnchor;

    /// <summary>The selected part of a line, or null when the selection is whole lines. This is the
    /// displayed text, so a tab reads as the spaces it was shown as.</summary>
    public string? SelectedText
    {
        get
        {
            if (!HasCharSelection || _doc is null) return null;
            string text = DisplayTextOf(_charLine);
            int from = Math.Clamp(Math.Min(_charAnchor, _charFocus), 0, text.Length);
            int to = Math.Clamp(Math.Max(_charAnchor, _charFocus), 0, text.Length);
            return to > from ? text[from..to] : null;
        }
    }

    /// <summary>Drops any part-of-a-line selection. Every way of moving around calls this: the range means
    /// nothing once the thing it was pointing at is no longer where the user is looking.</summary>
    private void ClearCharSelection()
    {
        if (_charLine < 0) return;
        _charLine = -1;
        _charColumn = -1;
        _charAnchor = _charFocus = 0;
        Invalidate();
    }

    /// <summary>Whether the log is being shown split into cells rather than as whole lines.</summary>
    private bool ColumnsOn => _doc is not null && _doc.Columns.Enabled;

    /// <summary>A row's text as it is drawn, so a character index means the same thing to the hit test, the
    /// painting and the clipboard. Tabs are expanded because that is how a whole line is drawn - but NOT
    /// while the line is split into cells, where each cell is drawn straight out of the line and a tab is
    /// usually the very thing the line was split on.</summary>
    private string DisplayText(long row) => DisplayTextOf(LineAt(row));

    /// <summary>The same, of a file line rather than of whatever row is showing it.</summary>
    private string DisplayTextOf(long line)
    {
        if (_doc is null || line < 0) return "";
        string raw = _doc.GetLineText(line);
        return ColumnsOn ? raw : Expand(raw);
    }

    /// <summary>Tabs as the spaces they are drawn as, so a character index means the same thing to the hit
    /// test, the painting and the clipboard.</summary>
    private string Expand(string text)
        => _settings.TabSize > 0 && text.Contains('\t')
            ? text.Replace("\t", new string(' ', _settings.TabSize))
            : text;

    /// <summary>Character index in a row's displayed text nearest to <paramref name="x"/>, by binary search
    /// on the measured width of the prefix - the same measurement the drawing uses, so the highlight lands
    /// exactly where the pointer did. <paramref name="y"/> picks the wrapped segment. Split into cells the
    /// cell under the pointer takes the place of the segment, and the index still counts from the start of
    /// the line, so everything downstream - the clipboard, the marks on other lines, a filter made from the
    /// selection - is unchanged.</summary>
    private int CharIndexAt(long row, int x, int y) => CharIndexAt(row, x, y, out _);

    private int CharIndexAt(long row, int x, int y, out int column)
    {
        column = -1;
        string text = DisplayText(row);
        if (text.Length == 0) return 0;
        var font = FontForRow(row, text);
        if (ColumnsOn)
        {
            column = ColumnUnder(x);
            return column < 0 ? 0 : CharIndexIn(row, column, x);
        }
        (int from, int to) = SegmentAt(row, text, font, y);
        int left = GutterWidth() - (Wrapping ? 0 : _hScroll);
        return from + NearestChar(text.AsSpan(from, to - from), x - left, font);
    }

    /// <summary>Which character of <paramref name="part"/> the pointer is nearest to, <paramref name="at"/>
    /// being measured from where that text starts on screen.</summary>
    private int NearestChar(ReadOnlySpan<char> part, int at, Font font)
    {
        int target = Math.Max(0, at);
        int lo = 0, hi = part.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (MeasureWidth(part[..mid], font) <= target) lo = mid; else hi = mid - 1;
        }
        // Round to the nearer edge of the character the pointer is inside, as a text box does.
        if (lo < part.Length)
        {
            int a = MeasureWidth(part[..lo], font), b = MeasureWidth(part[..(lo + 1)], font);
            if (target - a > b - target) lo++;
        }
        return lo;
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
        if (_doc is null) return FontRegular;
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

    /// <summary>Selecting part of a line works split into cells as well as whole; the cell simply takes the
    /// place of the line. What it never does is run from one cell into the next - the text between them is
    /// not on screen, so a selection spanning them could not be shown or read back honestly.</summary>
    private bool CharSelectionAvailable => _doc is not null;

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
    /// navigation having to work in occurrences. <paramref name="selected"/> is worked out once for the
    /// frame - decoding the line it came from again per row would be a string apiece for nothing.</summary>
    private void CollectHighlights(string text, bool caretRow, bool selectionLine, string? selected)
    {
        _highlights.Clear();
        var matcher = _highlight;
        if (matcher is null && selected is null) return;

        Color colour = caretRow ? _settings.FindCurrent : _settings.FindHighlight;
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
                if (!selectionLine || from + at != Math.Min(_charAnchor, _charFocus))
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
            g.FillRectangle(Fill(colour), x0, y, Math.Max(1, x1 - x0), _rowHeight);
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

    /// <summary>What the minimap needs to know about the selection having moved.</summary>
    internal long SelectionVersion => _sel.Version * 1_000_003L + _caretRow;

    /// <summary>The selected lines as ranges of display rows, worked out once rather than per pixel of the
    /// map: the map asks about hundreds of slots, and turning each slot's rows back into lines would be a
    /// rank and a select apiece. Stretches of the selection the view is hiding drop out here.</summary>
    internal void FillSelectedRowRanges(List<(long From, long To)> into)
    {
        into.Clear();
        if (_doc is null) return;
        foreach (var (a, b) in _sel.Ranges)
        {
            long from = _doc.RowAtOrAfterLine(a), to = _doc.RowAtOrAfterLine(b + 1);
            if (to > from) into.Add((from, to));
        }
    }

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

    /// <summary>The filters are painted differently but still match the same lines. Colour is resolved at
    /// paint time, so a repaint is the whole of it - nothing has to be re-evaluated or re-filtered.</summary>
    internal void RefreshColors()
    {
        _map?.InvalidateColors();
        Invalidate();
    }

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
        _anchorSelect = true;
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

    /// <summary>Keeps the caret on its original line as rows shift, and lets a lone selected line go with it
    /// when that line is no longer being shown at all. A selection of several lines is left exactly as it
    /// is: it is held in lines, so it needs no re-establishing.</summary>
    private void PinCaretToAnchor()
    {
        if (_doc is null || _anchorCaretLine < 0) return;
        long rows = _doc.RowCount;
        long caret = ResolveRow(_anchorCaretLine);
        if (caret < 0 || rows == 0) return;
        _caretRow = Math.Clamp(caret, 0, rows - 1);
        if (_anchorSelect && _sel.LineCount == 1) _sel.SetSingle(LineAt(_caretRow));
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
        EndRename(commit: false);
        _doc = doc;
        _settings = settings;
        _firstRow = 0;
        _hScroll = 0;
        _caretRow = -1;
        _naturalKey = null;
        _colWidths = [];
        _sel.Clear();
        ClearCharSelection();
        ClearViewAnchor();
        RebuildFonts();
        _map?.InvalidateColors();   // another file: nothing remembered about the last one still holds
        RefreshView();
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        RebuildFonts();
        // The settings decide what counts as "no colour at all", so the map's remembered colours were
        // worked out against them and have to be worked out again.
        _map?.InvalidateColors();
        RefreshView();
    }

    public void RebuildFonts()
    {
        for (int i = 0; i < _fonts.Length; i++) _fonts[i]?.Dispose();
        // After the fonts made from it, never before: a font keeps its family alive behind it.
        _fontFamily?.Dispose();
        float size = _settings.EffectiveFontSize;
        FontFamily family;
        try { family = new FontFamily(_settings.FontFamily); }
        catch { family = FontFamily.GenericMonospace; }
        _fontFamily = ReferenceEquals(family, FontFamily.GenericMonospace) ? null : family;
        for (int i = 0; i < _fonts.Length; i++)
            _fonts[i] = new Font(family, size,
                ((i & 1) != 0 ? FontStyle.Bold : 0) | ((i & 2) != 0 ? FontStyle.Italic : 0) |
                ((i & 4) != 0 ? FontStyle.Underline : 0));
        // Font.Height is the typeface's own line spacing, which for a monospaced face already includes
        // whatever gap its designer wanted between lines - so anything added here is the reader's choice,
        // not a correction. Two pixels used to be added unasked, costing a line in every eleven on screen.
        _rowHeight = Math.Max(FontRegular.Height + Math.Max(0, _settings.ExtraLineSpacing), 8);
        _charWidth = Math.Max(1, TextRenderer.MeasureText("0", FontRegular, new Size(1000, 100),
            TextFormatFlags.NoPadding).Width);
        for (int i = 0; i < _fonts.Length; i++)
            _charWidths[i] = Math.Max(1, TextRenderer.MeasureText("0", _fonts[i], new Size(1000, 100),
                TextFormatFlags.NoPadding).Width);
        // Asked of the shapes, not of the name: "Consolas" is fixed-pitch and "Segoe UI" is not, but a
        // family cannot be relied on to say so, and a wrong answer here sizes every column wrongly.
        _monospaced = TextRenderer.MeasureText("iiiiiiiiii", FontRegular, new Size(4000, 100), TextFormatFlags.NoPadding).Width
                   == TextRenderer.MeasureText("WWWWWWWWWW", FontRegular, new Size(4000, 100), TextFormatFlags.NoPadding).Width;
        _naturalKey = null;   // the widths the content asks for are measured in this font
        Invalidate();
    }

    /// <summary>Recomputes scrollbar ranges from the document and repaints. Call (on the UI thread)
    /// whenever counts change or the view mode/filters change.</summary>
    public void RefreshView()
    {
        long rows = _doc?.RowCount ?? 0;
        int visible = EffectiveVisibleRows;

        // A rename box belongs to a header that may no longer be there.
        if (_renameBox is not null && HeaderHeight == 0) EndRename(commit: false);
        // Nor does a selection outlive the mode it was made in: split into cells the indices are into the
        // line itself and belong to one cell, whole lines they are into the line with its tabs expanded.
        if (_charLine >= 0 && ColumnsOn != (_charColumn >= 0)) ClearCharSelection();

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
        // Split into columns, the content is as wide as the columns are - the lines behind them are not
        // what is drawn, and measuring those would give the scrollbar a range nothing on screen matches.
        if (_doc.Columns.Enabled) return TotalColumnsWidth();
        long rows = _doc.RowCount;
        int widest = 0;
        for (int i = 0; i < VisibleRowCount; i++)
        {
            long row = _firstRow + i;
            if (row >= rows) break;
            string text = Expand(_doc.GetLineText(_doc.RowToLine(row)));
            widest = Math.Max(widest, TextRenderer.MeasureText(text, FontRegular,
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

    /// <summary>How many times the view has actually repainted. A picture of it cannot answer that - drawing
    /// a control to a bitmap paints it whether or not anything asked it to.</summary>
    internal int PaintsForTesting => _paints;

    internal long CharOriginForTesting => _charOriginRow;

    /// <summary>Which file line the part-of-a-line selection is on, or -1. The whole point of the selection
    /// is that this answer does not change when the filters do.</summary>
    internal long CharSelectionLineForTesting => _charLine;

    /// <summary>Whether a file line is selected, whether or not the view is currently showing it.</summary>
    internal bool IsLineSelectedForTesting(long line) => _sel.Contains(line);

    internal int ViewportHeightForTesting => ViewportHeight;
    internal int RowHeightOfForTesting(long row) => RowHeightOf(row);
    internal Font FontForTesting => FontRegular;
    internal FontFamily? FontFamilyForTesting => _fontFamily;

    /// <summary>Top of a row as painted, so a check can aim at a wrapped row's second segment.</summary>
    internal int RowTopForTesting(long row)
    {
        foreach (var (r, top, _, _) in _layout)
            if (r == row) return top;
        return TextTop;
    }

    internal int RowHeightForTesting => _rowHeight;

    /// <summary>The face a row is actually drawn in, so a check can tell a style apart from something
    /// merely painted to look like one.</summary>
    internal Font FontForRowForTesting(long row) => FontForRow(row, DisplayText(row));

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
        _paints++;
        var g = e.Graphics;
        if (_doc is null) { g.Clear(_settings.Background); DrawFocusBar(g); return; }

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
        // What is picked out of a line, once for the frame rather than once per row: every row asks whether
        // it carries the same text, and answering it decodes the line the selection came from.
        string? selected = SelectedText;

        bool columns = _doc.Columns.Enabled;
        int runningMaxWidth = 0;

        if (columns) { EnsureColumnLayout(); DrawColumnHeader(g, gutter, contentW); }

        _layout.Clear();
        int atY = TextTop;
        int bottom = ClientSize.Height - BottomInset;
        // Kept for the whole frame, not read back per row: every read of Graphics.Clip builds a GDI+ region
        // with a finaliser behind it, and a screenful of rows is a few thousand of those a second.
        var paintClip = g.Clip;
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

            // Both of these are asked of the LINE, not of the row it landed on this frame: the filters move
            // every row about, and a highlight left on a row would end up over text nobody picked out.
            bool charSel = HasCharSelection && line == _charLine;
            bool selectedRow = !charSel && _sel.Contains(line);
            bool dim = !_doc.FilteredMode && !eval.Shown;

            Color back = selectedRow ? _settings.SelectionBack : ToColor(style.Background);
            Color fore = selectedRow ? _settings.SelectionFore : (dim ? _settings.DimForeground : ToColor(style.Foreground));

            Font font = SelectFont(style);
            int charWidth = CharWidthOf(FontIndex(style));
            string shown = columns ? text : Expand(text);
            int segments = columns ? 1 : WrapInto(shown, contentW, font, _segments);
            int rowHeight = segments * _rowHeight;
            _layout.Add((row, y, rowHeight, segments));

            var rowRect = new Rectangle(0, y, ClientSize.Width - RightGutterWidth, rowHeight);
            g.FillRectangle(Fill(back), rowRect);

            DrawMarkers(g, line, y, rowHeight);
            DrawLineNumber(g, line, y, rowHeight, selectedRow);

            var contentRect = new Rectangle(gutter, y, contentW, rowHeight);
            g.SetClip(contentRect);
            if (columns)
                DrawColumns(g, text, row, gutter, y, fore, font, charSel, selected);
            else
            {
                CollectHighlights(shown, row == _caretRow, charSel, selected);
                for (int s = 0; s < segments; s++)
                {
                    int from = _segments[s];
                    int to = s + 1 < _segments.Count ? _segments[s + 1] : shown.Length;
                    int sy = y + s * _rowHeight;
                    FillHighlights(g, shown, from, to, gutter, sy, font);
                    runningMaxWidth = Math.Max(runningMaxWidth, DrawSegment(g, shown, from, to, gutter, sy, fore, font, charWidth));
                    DrawHighlightText(g, shown, from, to, gutter, sy, font);
                    if (charSel) DrawCharSelection(g, shown, from, to, gutter, sy, font);
                }
            }
            g.Clip = paintClip;

            if (_doc.IsLineTruncated(line))
                TextRenderer.DrawText(g, " […]", FontItalic,
                    new Point(ClientSize.Width - RightGutterWidth - 40, y), Color.Gray);

            if (row == _caretRow && Focused)
                using (var pen = new Pen(Color.FromArgb(120, _settings.SelectionBack))) g.DrawRectangle(pen, 0, y, rowRect.Width - 1, rowHeight - 1);

            atY += rowHeight;
        }

        paintClip.Dispose();

        // Only the strip no row reached needs the background. Clearing the whole view first and then
        // painting a row over every part of it wrote every pixel of the window twice.
        if (atY < bottom)
            g.FillRectangle(Fill(_settings.Background), 0, atY, ClientSize.Width - RightGutterWidth, bottom - atY);

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
        g.FillRectangle(Fill(_settings.SelectionBack), 0, 0, LogicalToDeviceUnits(3), ClientSize.Height);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private Font SelectFont(ResolvedStyle s) => _fonts[FontIndex(s)];

    private static int FontIndex(ResolvedStyle s) =>
        (s.Bold ? 1 : 0) | (s.Italic ? 2 : 0) | (s.Underline ? 4 : 0);

    private void DrawColumnHeader(Graphics g, int gutter, int contentW)
    {
        int top = TopInset;
        var rect = new Rectangle(0, top, ClientSize.Width - RightGutterWidth, _rowHeight);
        using (var b = new SolidBrush(_settings.GutterBack)) g.FillRectangle(b, rect);
        using (var pen = new Pen(Color.FromArgb(210, 210, 210))) g.DrawLine(pen, 0, rect.Bottom - 1, rect.Width, rect.Bottom - 1);
        int x = gutter - _hScroll;
        int right = gutter + contentW;
        var clip = g.Clip;
        g.SetClip(new Rectangle(gutter, top, contentW, _rowHeight));
        var spec = _doc!.Columns;
        for (int i = 0; i < spec.Columns.Count; i++)
        {
            var def = spec.Columns[i];
            if (!def.Visible) continue;
            int w = _colWidths[i];
            if (x >= right) break;                 // everything from here on is off the right-hand side
            if (x + w <= gutter) { x += w; continue; }   // ...and this one is off the left
            // The one being carried is shaded, so it is clear which column the pointer has hold of.
            if (_colGesture == ColumnGesture.Reorder && i == _colIndex && _colMoved)
                using (var b = new SolidBrush(Color.FromArgb(60, _settings.SelectionBack)))
                    g.FillRectangle(b, x, top, w, _rowHeight - 1);
            TextRenderer.DrawText(g, def.Name, FontBold, new Rectangle(x + 3, top + 1, w - 6, _rowHeight - 2),
                Color.FromArgb(80, 80, 80), CellFlags(ColumnAlign.Left, x, w, gutter, right));
            // A hairline on every edge: without one there is nothing to aim the resize pointer at.
            using (var pen = new Pen(Color.FromArgb(210, 210, 210)))
                g.DrawLine(pen, x + w - 1, top + 2, x + w - 1, top + _rowHeight - 3);
            x += w;
        }
        g.Clip = clip;
    }

    private int TotalColumnsWidth()
    {
        EnsureColumnLayout();
        int w = 0;
        foreach (int width in _colWidths) w += width;
        return w;
    }

    /// <summary>Draws one row's cells. Only the ones on screen: with a line split into dozens of fields
    /// most of them are off the side, and a cell costs a text draw whether or not anyone can see it.
    /// It does NOT report a content width - the row is as wide as the columns are, which the caller reads
    /// once from <see cref="TotalColumnsWidth"/> rather than once per row.</summary>
    private void DrawColumns(Graphics g, string text, long row, int gutter, int y, Color fore, Font font,
                             bool charSel, string? selected)
    {
        Splitter().Split(text, _cols);
        CollectHighlights(text, row == _caretRow, charSel, selected);
        bool marks = _highlights.Count > 0 || charSel;
        int x = gutter - _hScroll;
        int right = gutter + ContentWidth;
        var spec = _doc!.Columns;
        for (int i = 0; i < spec.Columns.Count; i++)
        {
            var def = spec.Columns[i];
            if (!def.Visible) continue;
            int w = _colWidths[i];
            if (x >= right) break;
            if (x + w <= gutter) { x += w; continue; }
            var cell = new Rectangle(x + CellInset, y, w - 2 * CellInset, _rowHeight);
            var span = CellText(text, def, _cols);
            if (!marks)
            {
                TextRenderer.DrawText(g, span, font, cell, fore, CellFlags(def.Align, x, w, gutter, right));
            }
            else
            {
                var (from, to) = CellRange(def, _cols);
                int originX = CellTextOrigin(cell.Left, cell.Width, span, font, def.Align);
                FillCellHighlights(g, text, from, to, originX, cell, font);
                TextRenderer.DrawText(g, span, font, cell, fore, CellFlags(def.Align, x, w, gutter, right));
                DrawCellHighlightText(g, text, from, to, originX, cell, font);
                if (charSel && i == _charColumn) DrawCellCharSelection(g, text, from, to, originX, cell, font);
            }
            x += w;
        }
    }

    /// <summary>Where a character of a cell's text sits on screen.</summary>
    private int CellX(string line, int from, int index, int originX, Font font)
        => originX + MeasureWidth(line.AsSpan(from, Math.Max(0, index - from)), font);

    /// <summary>Fills whatever of the marked ranges falls inside this cell. Clamped to the cell's own box:
    /// a cell whose text is wider than it is would otherwise paint its marks over the column beside it.</summary>
    private void FillCellHighlights(Graphics g, string line, int from, int to, int originX, Rectangle cell, Font font)
    {
        foreach (var (at, len, colour) in _highlights)
        {
            int a = Math.Max(at, from), b = Math.Min(at + len, to);
            if (b <= a) continue;
            var rect = Rectangle.Intersect(cell, Span(a, b));
            if (rect.Width <= 0) continue;
            using var brush = new SolidBrush(colour);
            g.FillRectangle(brush, rect);
        }

        Rectangle Span(int a, int b)
        {
            int x0 = CellX(line, from, a, originX, font), x1 = CellX(line, from, b, originX, font);
            return new Rectangle(x0, cell.Top, Math.Max(1, x1 - x0), cell.Height);
        }
    }

    /// <summary>Re-draws marked text over its own fill in the ordinary colour, as the whole-line path does -
    /// a hit on a selected row would otherwise be white on orange.</summary>
    private void DrawCellHighlightText(Graphics g, string line, int from, int to, int originX, Rectangle cell, Font font)
    {
        foreach (var (at, len, _) in _highlights)
        {
            int a = Math.Max(at, from), b = Math.Min(at + len, to);
            if (b <= a) continue;
            DrawInCell(g, line.AsSpan(a, b - a), CellX(line, from, a, originX, font), cell, font, _settings.Foreground);
        }
    }

    private void DrawCellCharSelection(Graphics g, string line, int from, int to, int originX, Rectangle cell, Font font)
    {
        int a = Math.Clamp(Math.Min(_charAnchor, _charFocus), from, to);
        int b = Math.Clamp(Math.Max(_charAnchor, _charFocus), from, to);
        if (b <= a) return;
        int x0 = CellX(line, from, a, originX, font), x1 = CellX(line, from, b, originX, font);
        var rect = Rectangle.Intersect(cell, new Rectangle(x0, cell.Top, Math.Max(1, x1 - x0), cell.Height));
        if (rect.Width <= 0) return;
        using (var brush = new SolidBrush(_settings.SelectionBack)) g.FillRectangle(brush, rect);
        DrawInCell(g, line.AsSpan(a, b - a), x0, cell, font, _settings.SelectionFore);
    }

    /// <summary>Draws a stretch of a cell's text at an exact x, bounded by the cell so it cannot run into
    /// the next column. Text that would start left of the cell is left alone - it is already drawn, and
    /// moving it to fit would put it somewhere it does not belong.</summary>
    private static void DrawInCell(Graphics g, ReadOnlySpan<char> text, int x, Rectangle cell, Font font, Color colour)
    {
        if (x < cell.Left || x >= cell.Right) return;
        TextRenderer.DrawText(g, text, font, new Rectangle(x, cell.Top, cell.Right - x, cell.Height), colour,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
    }

    /// <summary>How to draw one cell.
    ///
    /// <see cref="TextFormatFlags.PreserveGraphicsClipping"/> is what makes text obey the clip the paint set
    /// around the content area - without it a cell hanging off the left edge is drawn straight over the
    /// marker and line-number margins. It is also the expensive part of a text draw (TextRenderer has to
    /// read the region off the Graphics and select it into the DC), and a cell that sits wholly inside the
    /// content area cannot escape it anyway, because DrawText already clips to the rectangle it is given.
    /// So only the one cell at each edge pays for it. Measured with a line split into 65 fields: 66 -> 49 ms
    /// a frame.</summary>
    private static TextFormatFlags CellFlags(ColumnAlign align, int x, int w, int gutter, int right)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        if (x < gutter || x + w > right) flags |= TextFormatFlags.PreserveGraphicsClipping;
        return align switch
        {
            ColumnAlign.Right => flags | TextFormatFlags.Right,
            ColumnAlign.Center => flags | TextFormatFlags.HorizontalCenter,
            _ => flags | TextFormatFlags.Left
        };
    }

    // ================= columns: resized, reordered, renamed and hidden from the header itself =================

    /// <summary>What a cell shows: the field the column says it shows, wherever that column has been
    /// carried to. A span rather than a string - the paint asks for one per cell per row, and a line split
    /// into dozens of fields would otherwise leave a screenful of substrings behind on every frame. The
    /// paint and the checks both come through here, so they cannot disagree.</summary>
    private static ReadOnlySpan<char> CellText(string line, ColumnDef def, List<ColumnValue> values)
        => def.Source >= 0 && def.Source < values.Count
            ? line.AsSpan(values[def.Source].Start, values[def.Source].Length)
            : default;

    /// <summary>The stretch of the line a column shows, as indices into the line.</summary>
    private static (int From, int To) CellRange(ColumnDef def, List<ColumnValue> values)
    {
        if (def.Source < 0 || def.Source >= values.Count) return (0, 0);
        var v = values[def.Source];
        return (v.Start, v.Start + v.Length);
    }

    /// <summary>Where a cell's text starts on screen. Not simply the left edge: a right- or centre-aligned
    /// cell draws its text elsewhere in the box, and the hit test and the marks have to agree with the
    /// glyphs.</summary>
    private int CellTextOrigin(int cellLeft, int cellWidth, ReadOnlySpan<char> text, Font font, ColumnAlign align)
    {
        if (align == ColumnAlign.Left) return cellLeft;
        int width = MeasureWidth(text, font);
        return align == ColumnAlign.Right ? cellLeft + cellWidth - width
                                          : cellLeft + (cellWidth - width) / 2;
    }

    /// <summary>The visible column <paramref name="x"/> is in, or the nearest one when the pointer is past
    /// either end - so a drag that wanders off the side keeps selecting in the cell it began in. -1 when
    /// there is no column to answer with.</summary>
    private int ColumnUnder(int x)
    {
        if (_doc is null) return -1;
        EnsureColumnLayout();
        var spec = _doc.Columns;
        int left = GutterWidth() - _hScroll, found = -1;
        for (int i = 0; i < spec.Columns.Count; i++)
        {
            if (!spec.Columns[i].Visible || _colWidths[i] <= 0) continue;
            // Take this cell if nothing has been taken yet - so a point left of the first one lands on it -
            // or once the pointer has reached it; stop as soon as the pointer is inside.
            if (found < 0 || x >= left) found = i;
            if (x < left + _colWidths[i]) break;
            left += _colWidths[i];
        }
        return found;
    }

    /// <summary>Where one cell of a row is: the stretch of the line it shows, and where that text starts on
    /// screen. The hit test and the marks both come through here, so neither can disagree with the glyphs.</summary>
    private (int From, int To, int OriginX) CellGeometry(int column, string text, Font font)
    {
        EnsureColumnLayout();
        var def = _doc!.Columns.Columns[column];
        Splitter().Split(text, _hitCells);
        var (from, to) = CellRange(def, _hitCells);
        int originX = CellTextOrigin(ColumnLeft(column) + CellInset, _colWidths[column] - 2 * CellInset,
                                     text.AsSpan(from, to - from), font, def.Align);
        return (from, to, originX);
    }

    /// <summary>Character index within one cell, counted from the start of the line. Used by a drag, which
    /// has to stay in the cell it began in however far sideways the pointer wanders.</summary>
    private int CharIndexIn(long row, int column, int x)
    {
        if (_doc is null || column < 0 || column >= _doc.Columns.Columns.Count) return 0;
        string text = DisplayText(row);
        var font = FontForRow(row, text);
        var (from, to, originX) = CellGeometry(column, text, font);
        return from + NearestChar(text.AsSpan(from, to - from), x - originX, font);
    }

    /// <summary>The gap between a cell's box and its text. One number, so the paint, the hit test and the
    /// marks cannot drift apart.</summary>
    private const int CellInset = 3;

    private readonly List<ColumnValue> _hitCells = new();
    private ColumnSplitter? _splitter;
    private string? _splitterKey;

    /// <summary>The splitter for the current spec. Kept rather than rebuilt because only the mode and the
    /// template are snapshotted inside one (the delimiter, the column list and their sources are read live),
    /// so those two are the whole of the key.</summary>
    private ColumnSplitter Splitter()
    {
        var spec = _doc!.Columns;
        string key = spec.Mode + "\u0001" + spec.Template;
        if (_splitter is null || !ReferenceEquals(_splitter.Spec, spec) || _splitterKey != key)
        {
            _splitter = new ColumnSplitter(spec);
            _splitterKey = key;
        }
        return _splitter;
    }

    private enum ColumnGesture { None, Resize, Reorder }

    /// <summary>How close to an edge counts as aiming at it.</summary>
    private int ResizeGrip => LogicalToDeviceUnits(4);

    /// <summary>The narrowest a column may be made. Wide enough that its edge can still be grabbed, which
    /// is what stops a column being dragged away to nothing and becoming unrecoverable.</summary>
    private int MinColumnWidth => Math.Max(LogicalToDeviceUnits(12), _charWidth * 2);

    /// <summary>Padding either side of a cell's text, so a column fitted to its content does not end
    /// exactly on the last glyph.</summary>
    private int CellPadding => Math.Max(LogicalToDeviceUnits(10), _charWidth);

    /// <summary>The width a column has been given, or 0 when it is free to be fitted. Characters win while
    /// a fixed-pitch font is in use so that zooming keeps the same fields visible.</summary>
    private int ExplicitWidth(ColumnDef def)
        => _monospaced && def.WidthChars > 0 ? def.WidthChars * _charWidth
         : def.Width > 0 ? def.Width : 0;

    /// <summary>What the content of each column asks for, measured from the rows on screen. Deliberately
    /// NOT redone as the view scrolls: a column that resized itself under the reader every few lines would
    /// be unusable. It is redone when the columns, the font or the file change, and on request.</summary>
    private void EnsureNaturalWidths()
    {
        var spec = _doc!.Columns;
        int n = spec.Columns.Count;
        string key = NaturalKey();
        if (_naturalKey == key && _colNatural.Length == n) return;
        _naturalKey = key;
        _colNatural = new int[n];

        for (int i = 0; i < n; i++)
            _colNatural[i] = TextRenderer.MeasureText(spec.Columns[i].Name, FontBold,
                new Size(int.MaxValue, _rowHeight), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width
                + CellPadding;

        var splitter = new ColumnSplitter(spec);
        var values = new List<ColumnValue>();
        long rows = _doc.RowCount;
        int sampled = 0;
        for (int r = 0; r < VisibleRowCount && sampled < 400; r++)
        {
            long row = _firstRow + r;
            if (row < 0 || row >= rows) break;
            string text = _doc.GetLineText(_doc.RowToLine(row));
            splitter.Split(text, values);
            sampled++;
            for (int i = 0; i < n; i++)
            {
                if (!spec.Columns[i].Visible) continue;
                int src = spec.Columns[i].Source;
                if (src < 0 || src >= values.Count) continue;
                int w = TextRenderer.MeasureText(text.AsSpan(values[src].Start, values[src].Length), FontRegular,
                    new Size(int.MaxValue, _rowHeight), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width
                    + CellPadding;
                if (w > _colNatural[i]) _colNatural[i] = w;
            }
        }

        // Nothing to measure yet (a file still opening, or an empty one): fall back to something usable
        // rather than to nothing, and ask again once there are rows - the key says so.
        for (int i = 0; i < n; i++)
            if (_colNatural[i] <= 0) _colNatural[i] = DefaultColumnWidth;
    }

    /// <summary>What the measured widths depend on. Not the scroll position, and not the exact row count -
    /// only whether there is anything to measure at all.</summary>
    private string NaturalKey()
    {
        var spec = _doc!.Columns;
        var sb = new StringBuilder();
        sb.Append(_settings.FontFamily).Append('|').Append(_settings.EffectiveFontSize).Append('|')
          .Append(spec.Mode).Append('|').Append(spec.Delimiter).Append('|').Append(spec.Template).Append('|')
          .Append(spec.MaxSplits).Append('|').Append(spec.CollapseConsecutive).Append('|')
          .Append(_doc.RowCount > 0 ? '1' : '0').Append('|');
        foreach (var c in spec.Columns) sb.Append(c.Name).Append(c.Visible ? '+' : '-').Append(c.Source).Append('\u0001');
        return sb.ToString();
    }

    /// <summary>Resolves every column's drawn width. Cheap, and redone on every use, so a resize or a
    /// window change is felt immediately; only the content measurement behind it is cached.</summary>
    private void EnsureColumnLayout()
    {
        var spec = _doc!.Columns;
        int n = spec.Columns.Count;
        if (_colWidths.Length != n) _colWidths = new int[n];
        if (n == 0) return;
        // A column added in code says nothing about which field it shows; settle that here, once, rather
        // than leaving every such spec drawing empty cells.
        spec.NormalizeSources();
        EnsureNaturalWidths();

        var wanted = new List<int>(n);
        var auto = new List<bool>(n);
        var at = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            _colWidths[i] = 0;
            if (!spec.Columns[i].Visible) continue;
            int explicitWidth = ExplicitWidth(spec.Columns[i]);
            at.Add(i);
            auto.Add(explicitWidth == 0);
            wanted.Add(explicitWidth == 0 ? _colNatural[i] : explicitWidth);
        }
        if (at.Count == 0) return;

        var fitted = ColumnLayout.Fit(wanted, auto, ContentWidth, MinColumnWidth);
        for (int k = 0; k < at.Count; k++) _colWidths[at[k]] = fitted[k];
    }

    /// <summary>Left edge of a column in client coordinates, scrolling included.</summary>
    private int ColumnLeft(int index)
    {
        EnsureColumnLayout();
        int x = GutterWidth() - _hScroll;
        for (int i = 0; i < index && i < _colWidths.Length; i++) x += _colWidths[i];
        return x;
    }

    private Rectangle ColumnHeaderRect(int index)
        => new(ColumnLeft(index), TopInset, Math.Max(0, _colWidths[index]), _rowHeight);

    /// <summary>Whether a point is on the column header - the strip the gestures below belong to.</summary>
    private bool InColumnHeader(int y)
        => HeaderHeight > 0 && y >= TopInset && y < TopInset + HeaderHeight;

    /// <summary>The column whose right-hand edge is under <paramref name="x"/>, or -1. Hidden columns have
    /// no edge, so resizing one is not offered - it is not on screen to be aimed at.</summary>
    private int DividerAt(int x)
    {
        EnsureColumnLayout();
        int edge = GutterWidth() - _hScroll;
        for (int i = 0; i < _colWidths.Length; i++)
        {
            if (_colWidths[i] <= 0) continue;
            edge += _colWidths[i];
            if (Math.Abs(x - edge) <= ResizeGrip) return i;
        }
        return -1;
    }

    /// <summary>The column under <paramref name="x"/>, or -1 past the last one.</summary>
    private int ColumnAt(int x)
    {
        EnsureColumnLayout();
        int left = GutterWidth() - _hScroll;
        for (int i = 0; i < _colWidths.Length; i++)
        {
            if (_colWidths[i] <= 0) continue;
            if (x >= left && x < left + _colWidths[i]) return i;
            left += _colWidths[i];
        }
        return -1;
    }

    private List<int> VisibleColumnIndices()
    {
        var list = new List<int>();
        var spec = _doc!.Columns;
        for (int i = 0; i < spec.Columns.Count; i++) if (spec.Columns[i].Visible) list.Add(i);
        return list;
    }

    /// <summary>Raised whenever the columns are changed from the header, so the window can record that
    /// there is something to save.</summary>
    internal event Action? ColumnsChanged;

    /// <summary>Raised by the header's "Columns…" entry, so the full settings still have one home.</summary>
    internal event Action? ColumnSettingsRequested;

    /// <summary>One place every in-view column edit ends: the drawn widths are stale, the header and every
    /// row have to be redrawn, and whoever owns the file has to know it now differs from what is on disk.
    /// It does NOT re-measure the content - the measurement's own key covers the changes that could affect
    /// it, so a resize drag does not pay for one on every step of the gesture.</summary>
    private void ColumnsEdited()
    {
        _maxContentWidth = 0;
        EnsureColumnLayout();
        UpdateHScroll();
        Invalidate();
        Update();                    // a held drag never empties the queue, so a repaint has to be pushed
        ColumnsChanged?.Invoke();
    }

    /// <summary>Gives a column a width, in whole characters where that means anything.</summary>
    private void SetColumnWidth(int index, int pixels)
    {
        var def = _doc!.Columns.Columns[index];
        pixels = Math.Max(MinColumnWidth, pixels);
        if (_monospaced)
        {
            pixels = ColumnLayout.SnapToChars(pixels, _charWidth);
            def.WidthChars = Math.Max(1, pixels / _charWidth);
        }
        else def.WidthChars = 0;
        def.Width = pixels;
    }

    /// <summary>Sizes a column to what is in it, which is what double-clicking its edge means.</summary>
    internal void FitColumnToContent(int index)
    {
        if (_doc is null || index < 0 || index >= _doc.Columns.Columns.Count) return;
        _naturalKey = null;
        EnsureNaturalWidths();
        SetColumnWidth(index, _colNatural[index]);
        ColumnsEdited();
    }

    /// <summary>Hands every column back to the layout, so the row fills the window again.</summary>
    internal void FitColumnsToWindow()
    {
        if (_doc is null) return;
        foreach (var def in _doc.Columns.Columns) { def.Width = 0; def.WidthChars = 0; }
        _naturalKey = null;
        ColumnsEdited();
    }

    internal void SetColumnVisible(int index, bool visible)
    {
        if (_doc is null || index < 0 || index >= _doc.Columns.Columns.Count) return;
        // The last one standing may not be hidden: there would then be no header left to unhide it from.
        if (!visible && VisibleColumnIndices().Count <= 1) return;
        if (_doc.Columns.Columns[index].Visible == visible) return;
        _doc.Columns.Columns[index].Visible = visible;
        ColumnsEdited();
    }

    internal void SetColumnAlign(int index, ColumnAlign align)
    {
        if (_doc is null || index < 0 || index >= _doc.Columns.Columns.Count) return;
        if (_doc.Columns.Columns[index].Align == align) return;
        _doc.Columns.Columns[index].Align = align;
        ColumnsEdited();
    }

    /// <summary>Moves a column so it sits at <paramref name="toVisiblePosition"/> among the visible ones.
    /// Hidden columns keep their place in the list rather than being dragged along by a move they had no
    /// part in.</summary>
    internal void MoveColumnTo(int index, int toVisiblePosition)
    {
        if (_doc is null) return;
        var cols = _doc.Columns.Columns;
        if (index < 0 || index >= cols.Count) return;
        var def = cols[index];
        cols.RemoveAt(index);
        var visible = new List<int>();
        for (int i = 0; i < cols.Count; i++) if (cols[i].Visible) visible.Add(i);
        int insertAt = toVisiblePosition >= 0 && toVisiblePosition < visible.Count ? visible[toVisiblePosition] : cols.Count;
        cols.Insert(insertAt, def);
    }

    /// <summary>The gesture the pointer would start on the header, if any. Returns false when the press
    /// belongs to the log rather than to the header.</summary>
    private bool HandleHeaderMouseDown(MouseEventArgs e, int clicks)
    {
        if (_doc is null || !InColumnHeader(e.Y)) return false;
        EndRename(commit: true);

        if (e.Button == MouseButtons.Right)
        {
            ShowColumnMenu(e.Location);
            return true;
        }
        if (e.Button != MouseButtons.Left) return true;

        int divider = DividerAt(e.X);
        if (divider >= 0)
        {
            // On an edge: a double-click sizes that column to its content, a drag sizes it by hand.
            if (clicks >= 2) { FitColumnToContent(divider); return true; }
            _colGesture = ColumnGesture.Resize;
            _colIndex = divider;
            _colGrabX = e.X;
            _colGrabWidth = _colWidths[divider];
            _colMoved = false;
            Capture = true;
            return true;
        }

        int column = ColumnAt(e.X);
        if (column < 0) return true;
        if (clicks >= 2) { BeginRename(column); return true; }
        _colGesture = ColumnGesture.Reorder;
        _colIndex = column;
        _colGrabX = e.X;
        _colMoved = false;
        Capture = true;
        return true;
    }

    private void HandleHeaderMouseMove(MouseEventArgs e)
    {
        switch (_colGesture)
        {
            case ColumnGesture.Resize:
                {
                    int width = _colGrabWidth + (e.X - _colGrabX);
                    var def = _doc!.Columns.Columns[_colIndex];
                    int before = ExplicitWidth(def);
                    SetColumnWidth(_colIndex, width);
                    if (ExplicitWidth(def) != before) { _colMoved = true; ColumnsEdited(); }
                    break;
                }
            case ColumnGesture.Reorder:
                {
                    if (!_colMoved && Math.Abs(e.X - _colGrabX) < SystemInformation.DragSize.Width) break;
                    _colMoved = true;
                    var visible = VisibleColumnIndices();
                    int from = visible.IndexOf(_colIndex);
                    if (from < 0) break;
                    var widths = visible.Select(i => _colWidths[i]).ToList();
                    int to = ColumnLayout.DropTarget(widths, from, e.X - (GutterWidth() - _hScroll));
                    if (to != from) { MoveColumnTo(_colIndex, to); _colIndex = VisibleColumnIndices()[to]; ColumnsEdited(); }
                    else Invalidate();
                    break;
                }
            default:
                SetCursorTo(InColumnHeader(e.Y) && DividerAt(e.X) >= 0 ? Cursors.VSplit : Cursors.Default);
                break;
        }
    }

    /// <summary>Only when it differs: assigning Cursor talks to the window every time, and this runs on
    /// every pointer move.</summary>
    private void SetCursorTo(Cursor cursor) { if (Cursor != cursor) Cursor = cursor; }

    private void EndColumnGesture()
    {
        if (_colGesture == ColumnGesture.None) return;
        bool moved = _colMoved;
        _colGesture = ColumnGesture.None;
        _colIndex = -1;
        _colMoved = false;
        Capture = false;
        SetCursorTo(Cursors.Default);
        if (moved) ColumnsEdited();
        else Invalidate();
    }

    // ---- renaming, in place ----

    /// <summary>Puts an edit box over the header cell, which is where the name is read, so renaming needs
    /// no dialog and no hunting for the setting.</summary>
    internal void BeginRename(int index)
    {
        if (_doc is null || index < 0 || index >= _doc.Columns.Columns.Count) return;
        if (!_doc.Columns.Columns[index].Visible) return;
        EndRename(commit: true);
        var rect = ColumnHeaderRect(index);
        if (rect.Width <= 0) return;
        _renameIndex = index;
        _renameBox = new TextBox
        {
            Text = _doc.Columns.Columns[index].Name,
            Bounds = rect,
            BorderStyle = BorderStyle.FixedSingle,
            Font = FontRegular
        };
        _renameBox.KeyDown += (_, ke) =>
        {
            if (ke.KeyCode == Keys.Enter) { ke.Handled = ke.SuppressKeyPress = true; EndRename(commit: true); Focus(); }
            else if (ke.KeyCode == Keys.Escape) { ke.Handled = ke.SuppressKeyPress = true; EndRename(commit: false); Focus(); }
        };
        _renameBox.LostFocus += (_, _) => EndRename(commit: true);
        Controls.Add(_renameBox);
        _renameBox.BringToFront();
        _renameBox.Focus();
        _renameBox.SelectAll();
    }

    /// <summary>Takes the edit box away, keeping what was typed or discarding it. Guarded against re-entry
    /// on purpose: committing takes the focus away, which raises LostFocus, which asks to commit again.</summary>
    internal void EndRename(bool commit)
    {
        if (_renameBox is null || _renaming) return;
        _renaming = true;
        try
        {
            var box = _renameBox;
            _renameBox = null;
            int index = _renameIndex;
            _renameIndex = -1;
            string name = box.Text.Trim();
            Controls.Remove(box);
            box.Dispose();
            if (commit && index >= 0 && index < _doc!.Columns.Columns.Count && name.Length > 0
                && _doc.Columns.Columns[index].Name != name)
            {
                _doc.Columns.Columns[index].Name = name;
                ColumnsEdited();
            }
            else Invalidate();
        }
        finally { _renaming = false; }
    }

    internal bool IsRenamingForTesting => _renameBox is not null;

    // ---- the header's own menu: everything about a column, where the column is ----

    private void ShowColumnMenu(Point at)
    {
        var menu = BuildColumnMenu(ColumnAt(at.X));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, at);
    }

    /// <summary>The header's menu for the column at <paramref name="index"/> (-1 past the last one).
    ///
    /// Ticking a column off must NOT put the menu away: turning three of them off should be one visit, not
    /// three. So no item click closes it by itself and every entry that is a COMMAND closes it explicitly.
    /// Written that way round because it then does not depend on whether WinForms raises Click before or
    /// after it tries to close - either order ends with the menu gone exactly once.</summary>
    private ContextMenuStrip BuildColumnMenu(int index)
    {
        var spec = _doc!.Columns;
        // A check margin and no image one: the list at the top is a set of ticks saying which columns are
        // shown, and turning the image margin off takes away the very place a tick is drawn.
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };

        // Every column, ticked or not - a hidden column has no header to right-click, so this list is the
        // only way back to one.
        var ticks = new List<ToolStripMenuItem>();
        var forThisColumn = new List<(ToolStripMenuItem Item, bool NeedsAnother)>();
        void SyncTicks()
        {
            // The last column standing may not be hidden, so a tick is not always honoured; and every row's
            // "can this one go?" changes as the others come and go.
            for (int i = 0; i < ticks.Count; i++)
            {
                ticks[i].Checked = spec.Columns[i].Visible;
                ticks[i].Enabled = !spec.Columns[i].Visible || VisibleColumnIndices().Count > 1;
            }
            // Because the menu outlives a tick, the column it was opened over can be hidden while it is
            // still up - and renaming or fitting a column nobody can see does nothing.
            bool shown = index >= 0 && spec.Columns[index].Visible;
            foreach (var (item, needsAnother) in forThisColumn)
                item.Enabled = shown && (!needsAnother || VisibleColumnIndices().Count > 1);
        }

        for (int i = 0; i < spec.Columns.Count; i++)
        {
            int which = i;
            var item = new ToolStripMenuItem(spec.Columns[i].Name.Length > 0 ? spec.Columns[i].Name : $"Column {i + 1}")
            {
                CheckOnClick = true
            };
            item.Click += (_, _) => { SetColumnVisible(which, item.Checked); SyncTicks(); };
            ticks.Add(item);
            menu.Items.Add(item);
        }
        SyncTicks();

        if (index >= 0)
        {
            string name = spec.Columns[index].Name;
            menu.Items.Add(new ToolStripSeparator());
            var rename = Entry($"&Rename \"{name}\"…", () => BeginRename(index));
            var hide = Entry($"&Hide \"{name}\"", () => SetColumnVisible(index, false));
            var fit = Entry($"Fit \"{name}\" to &Content", () => FitColumnToContent(index));
            var align = new ToolStripMenuItem("&Align");
            foreach (var (text, value) in new[] { ("&Left", ColumnAlign.Left), ("&Right", ColumnAlign.Right), ("&Centre", ColumnAlign.Center) })
            {
                var a = value;
                var item = new ToolStripMenuItem(text) { Checked = spec.Columns[index].Align == a };
                item.Click += (_, _) => { menu.Close(); SetColumnAlign(index, a); };
                align.DropDownItems.Add(item);
            }
            forThisColumn.Add((rename, false));
            forThisColumn.Add((hide, true));
            forThisColumn.Add((fit, false));
            forThisColumn.Add((align, false));
            menu.Items.Add(rename);
            menu.Items.Add(hide);
            menu.Items.Add(fit);
            menu.Items.Add(align);
        }
        SyncTicks();

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Entry("&Fit All Columns to Window", FitColumnsToWindow));
        menu.Items.Add(Entry("&Columns…", () => ColumnSettingsRequested?.Invoke()));
        return menu;

        ToolStripMenuItem Entry(string text, Action run, bool enabled = true)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled };
            item.Click += (_, _) => { menu.Close(); run(); };
            return item;
        }
    }
    /// <summary>The header menu as it would be shown over one column, so a check can read what it offers
    /// and press its entries.</summary>
    internal ContextMenuStrip ColumnMenuForTesting(int index) => BuildColumnMenu(index);

    /// <summary>Whether a menu would stay up when one of its items is clicked. Raises the real Closing
    /// event, which is where the decision is made.</summary>
    internal static bool StaysOpenOnItemClickForTesting(ContextMenuStrip menu)
    {
        var e = new ToolStripDropDownClosingEventArgs(ToolStripDropDownCloseReason.ItemClicked);
        typeof(ToolStripDropDown).GetMethod("OnClosing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(menu, [e]);
        return e.Cancel;
    }

    /// <summary>...and that it still goes away when something actually asks it to.</summary>
    internal static bool ClosesWhenAskedForTesting(ContextMenuStrip menu)
    {
        var e = new ToolStripDropDownClosingEventArgs(ToolStripDropDownCloseReason.CloseCalled);
        typeof(ToolStripDropDown).GetMethod("OnClosing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(menu, [e]);
        return !e.Cancel;
    }

    // ---- seams, so the gestures above can be checked without a mouse ----

    internal int ColumnWidthForTesting(int index) { EnsureColumnLayout(); return index >= 0 && index < _colWidths.Length ? _colWidths[index] : -1; }
    internal int ColumnLeftForTesting(int index) => ColumnLeft(index);
    internal int ColumnHeaderYForTesting => TopInset + _rowHeight / 2;
    internal int CharWidthForTesting => _charWidth;
    internal bool MonospacedForTesting => _monospaced;
    internal int MinColumnWidthForTesting => MinColumnWidth;
    internal int ContentWidthForTesting => ContentWidth;
    internal int NaturalWidthForTesting(int index) { EnsureNaturalWidths(); return _colNatural[index]; }
    internal string[] ColumnNamesForTesting => _doc is null ? [] : _doc.Columns.Columns.Select(c => c.Name).ToArray();
    internal int DividerAtForTesting(int x) => DividerAt(x);
    internal int ColumnAtForTesting(int x) => ColumnAt(x);

    /// <summary>What one cell of one row is drawn with - the paint's own lookup, not a copy of it.</summary>
    internal string CellTextForTesting(long row, int column)
    {
        if (_doc is null || column < 0 || column >= _doc.Columns.Columns.Count) return "";
        string text = _doc.GetLineText(_doc.RowToLine(row));
        Splitter().Split(text, _cols);
        return CellText(text, _doc.Columns.Columns[column], _cols).ToString();
    }

    /// <summary>Screen x of a character inside one cell, so a check can aim at one rather than guess.</summary>
    internal int XForCharInCellForTesting(long row, int column, int index)
    {
        string text = DisplayText(row);
        var font = FontForRow(row, text);
        var (from, _, originX) = CellGeometry(column, text, font);
        return CellX(text, from, index, originX, font);
    }

    /// <summary>The stretch of a row's line that one cell shows.</summary>
    internal (int From, int To) CellRangeForTesting(long row, int column)
    {
        string text = DisplayText(row);
        var (from, to, _) = CellGeometry(column, text, FontForRow(row, text));
        return (from, to);
    }

    /// <summary>Which cell the part-of-a-line selection lives in, or -1 for whole lines.</summary>
    internal int CharColumnForTesting => _charColumn;

    /// <summary>Picks out characters <paramref name="from"/>..<paramref name="to"/> of one cell's text,
    /// so a render can be captured without a mouse.</summary>
    internal void SelectPartOfCellForTesting(long row, int column, int from, int to)
    {
        var (start, end) = CellRangeForTesting(row, column);
        _charLine = LineAt(row);
        _charColumn = column;
        _charAnchor = Math.Clamp(start + from, start, end);
        _charFocus = Math.Clamp(start + to, start, end);
        _sel.SetSingle(_charLine);
        _caretRow = row;
        Invalidate();
    }

    internal void PressHeaderForTesting(int x, int clicks = 1)
    {
        ForgetLastClick();
        HandleHeaderMouseDown(new MouseEventArgs(MouseButtons.Left, clicks, x, ColumnHeaderYForTesting, 0), clicks);
    }

    internal void DragHeaderToForTesting(int x)
        => HandleHeaderMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x, ColumnHeaderYForTesting, 0));

    internal void ReleaseHeaderForTesting() => EndColumnGesture();

    /// <summary>A whole drag of a column edge, from grab to release.</summary>
    internal void DragColumnEdgeForTesting(int index, int toX)
    {
        PressHeaderForTesting(ColumnLeft(index) + _colWidths[index]);
        DragHeaderToForTesting(toX);
        EndColumnGesture();
    }

    internal void SetRenameTextForTesting(string text) { if (_renameBox is not null) _renameBox.Text = text; }

    /// <summary>Gives a column a width outright, so a check can set one up without a gesture.</summary>
    internal void SetColumnWidthForTesting(int index, int pixels) { SetColumnWidth(index, pixels); ColumnsEdited(); }

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
    private int DrawSegment(Graphics g, string text, int from, int to, int gutter, int y, Color fore, Font font, int charWidth)
    {
        string part = text[from..to];
        int x = gutter - (Wrapping ? 0 : _hScroll);
        TextRenderer.DrawText(g, part, font, new Point(x, y), fore, TextFlags);
        if (Wrapping) return 0;   // nothing scrolls sideways while wrapping, so nothing to measure against
        return DrawnWidth(part, font, charWidth) + 8;
    }

    /// <summary>
    /// How wide a stretch of text is drawn. Only the horizontal scrollbar's range is built from this, so it
    /// is answered the cheapest way that is still exact.
    /// <para>Measuring it is not cheap: <see cref="TextRenderer.MeasureText(string, Font)"/> lays the text
    /// out through the same call that draws it, which MEASURED a sixth of every repaint on a full screen of
    /// long lines. In a fixed-pitch face every plain-ASCII character is exactly one character wide -
    /// verified exact against the measurement for Consolas, Courier New, Lucida Console and Cascadia Mono at
    /// five sizes and every combination of bold, italic and underline - so the common case is a
    /// multiplication. Anything else (a proportional face, or a character that may be drawn from a linked
    /// font) is still measured, because those really do disagree, by tens of pixels.</para>
    /// </summary>
    private int DrawnWidth(ReadOnlySpan<char> text, Font font, int charWidth)
        => charWidth > 0 && text.IndexOfAnyExceptInRange(' ', '~') < 0
            ? text.Length * charWidth
            : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, _rowHeight),
                  TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

    /// <summary>The width of one character when every character is the same width, else 0.</summary>
    private int CharWidthOf(int fontIndex) => _monospaced ? _charWidths[fontIndex] : 0;

    /// <summary>A brush for a colour, kept rather than made. A frame fills a few hundred rectangles in a
    /// handful of colours, and every one of those used to be a fresh GDI+ object and a finaliser.</summary>
    private SolidBrush Fill(Color colour)
    {
        if (_brushes.TryGetValue(colour.ToArgb(), out var brush)) return brush;
        // A view has as many colours as it has filters; anything beyond that is a settings change, and
        // starting again is cheaper than remembering for ever.
        if (_brushes.Count > 512) { foreach (var b in _brushes.Values) b.Dispose(); _brushes.Clear(); }
        return _brushes[colour.ToArgb()] = new SolidBrush(colour);
    }

    /// <summary>What the horizontal scrollbar's range is built from, however it was arrived at.</summary>
    internal int DrawnWidthForTesting(string text, int fontIndex)
        => DrawnWidth(text, _fonts[fontIndex], CharWidthOf(fontIndex));

    /// <summary>The same width, always measured - what the shortcut has to agree with.</summary>
    internal int MeasuredWidthForTesting(string text, int fontIndex)
        => DrawnWidth(text, _fonts[fontIndex], 0);

    /// <summary>Whether the shortcut was taken, so a check can prove it is doing something.</summary>
    internal bool WidthWasArithmeticForTesting(string text, int fontIndex)
        => CharWidthOf(fontIndex) > 0 && text.AsSpan().IndexOfAnyExceptInRange(' ', '~') < 0;


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
        g.FillRectangle(Fill(_settings.GutterBack), 0, y, MarkerGutterWidth, height);
        byte mask = _doc.Markers.MaskOf(line);
        if (mask == 0) return;
        for (int m = 0; m < 8; m++)
        {
            if ((mask & (1 << m)) == 0) continue;
            g.FillRectangle(Fill(AppSettings.MarkerColors[m]), 3 + m * 5, y + 2, 4, _rowHeight - 4);
        }
    }

    private void DrawLineNumber(Graphics g, long line, int y, int height, bool selected)
    {
        int lnw = LineNumberGutterWidth;
        if (lnw == 0) return;
        int x = MarkerGutterWidth;
        // The whole height of the row, not one line of it: a wrapped row is several lines tall, and the
        // segments below the first would otherwise keep the row's own fill - or its selection colour.
        g.FillRectangle(Fill(_settings.GutterBack), x, y, lnw, height);
        var color = selected ? _settings.SelectionBack : _settings.LineNumberColor;
        TextRenderer.DrawText(g, (line + 1).ToString(), FontRegular, new Rectangle(x, y, lnw - 6, _rowHeight),
            color, TextFormatFlags.NoPadding | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    // ---- input ----

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (_doc is null) { base.OnMouseDown(e); return; }

        // Windows only ever reports one or two clicks, so a triple has to be counted here. Counted for the
        // left button only, or a right-click would leave a stray count behind for the next real one.
        if (e.Button == MouseButtons.Left)
        {
            bool repeat = (DateTime.UtcNow - _lastClickAt).TotalMilliseconds <= SystemInformation.DoubleClickTime
                          && Math.Abs(e.X - _lastClickAtPoint.X) <= SystemInformation.DragSize.Width
                          && Math.Abs(e.Y - _lastClickAtPoint.Y) <= SystemInformation.DragSize.Height;
            _clickCount = repeat ? Math.Min(3, _clickCount + 1) : 1;
            _lastClickAt = DateTime.UtcNow;
            _lastClickAtPoint = e.Location;
        }

        if (HandleHeaderMouseDown(e, _clickCount)) return;
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }

        long row = RowAtY(e.Y); // resolves against the live viewport before stabilization is dropped
        ClearViewAnchor();
        if (row < 0 || row >= _doc.RowCount) return;
        long line = LineAt(row);

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
        _carriedSelection = line == _charLine && HasCharSelection ? SelectedText : null;

        // A plain click - and a triple click - means the whole line, which is also where a drag starts from.
        ClearCharSelection();
        if ((ModifierKeys & Keys.Shift) != 0 && _sel.Anchor >= 0) _sel.SetRange(_sel.Anchor, line);
        else if ((ModifierKeys & Keys.Control) != 0) _sel.ToggleSingle(line);
        else _sel.SetSingle(line);

        _caretRow = row;
        _dragging = true;
        _charDragging = false;
        _charOriginRow = -1;
        _charOriginColumn = -1;
        if (CharSelectionAvailable && (ModifierKeys & (Keys.Shift | Keys.Control)) == 0)
        {
            // Armed, not shown: a drag that stays on this row turns into a character selection, one that
            // leaves it selects whole rows, and one that comes back picks the characters up again.
            _charLine = line;
            _charOriginRow = row;
            _charAnchor = _charFocus = _charOriginAt = CharIndexAt(row, e.X, e.Y, out _charColumn);
            _charOriginColumn = _charColumn;
        }
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_doc is not null && _doc.Columns.Enabled) HandleHeaderMouseMove(e);
        else SetCursorTo(Cursors.Default);
        if (_colGesture != ColumnGesture.None) return;

        TrackHover(e.Location);
        if (_dragging && _doc is not null)
        {
            long row = Math.Clamp(RowAtY(e.Y), 0, Math.Max(0, _doc.RowCount - 1));
            if (row == _charOriginRow)
            {
                // Back on the row it started from, so it is a selection within that line again - and, when
                // the line is split into cells, within the cell it started in.
                _charLine = LineAt(row);
                _charColumn = _charOriginColumn;
                _charAnchor = _charOriginAt;
                int at = _charColumn >= 0 ? CharIndexIn(row, _charColumn, e.X)
                                          : CharIndexAt(row, e.X, e.Y);
                if (at != _charFocus || _caretRow != row || _sel.LineCount != 1)
                {
                    _charFocus = at;
                    _charDragging = true;
                    _caretRow = row;
                    _sel.SetSingle(_charLine);
                    Invalidate();
                    Update();
                    SelectionChanged?.Invoke();
                }
            }
            else if (row != _caretRow || _charLine >= 0)
            {
                // Left the row: this is a selection of whole lines after all.
                ClearCharSelection();
                _sel.SetRange(_sel.Anchor, LineAt(row));
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
        if (_colGesture != ColumnGesture.None) { EndColumnGesture(); return; }
        _dragging = false;
        _charOriginRow = -1;
        _charOriginColumn = -1;
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
            foreach (var f in _fonts) f?.Dispose();
            foreach (var b in _brushes.Values) b.Dispose();
            _brushes.Clear();
            _fontFamily?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        HideTip();
        if (_colGesture == ColumnGesture.None) SetCursorTo(Cursors.Default);
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
            case Keys.A when e.Control: SelectAll(); break;
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
        foreach (long line in SelectedLines(CopyLineCap)) _doc.Markers.Toggle(line, index);
        if (_sel.IsEmpty && _caretRow >= 0) _doc.Markers.Toggle(_doc.RowToLine(_caretRow), index);
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
        _sel.SetSingle(LineAt(row));
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
        if (extend && _sel.Anchor >= 0) _sel.SetRange(_sel.Anchor, LineAt(row));
        else _sel.SetSingle(LineAt(row));
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

    public void SelectAll()
    {
        if (_doc is null) return;
        ClearCharSelection();
        _sel.SelectAll(_doc.CompletedLineCount);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    /// <summary>Clears the current selection.</summary>
    public void ClearSelection() { _sel.Clear(); Invalidate(); SelectionChanged?.Invoke(); }

    /// <summary>How many selected lines the view is showing. A selection is a stretch of the log, so what
    /// is hidden is not counted - two rank lookups a range, rather than a walk.</summary>
    public long SelectedCount
    {
        get
        {
            if (_doc is null) return 0;
            long n = 0;
            foreach (var (a, b) in _sel.Ranges)
                n += Math.Max(0, _doc.RowAtOrAfterLine(b + 1) - _doc.RowAtOrAfterLine(a));
            return n;
        }
    }

    /// <summary>The selected lines the view is showing, in order. Walked through the rows rather than
    /// through the lines, so a selection spanning a hidden million costs only what it yields.</summary>
    private IEnumerable<long> SelectedLines(long cap)
    {
        if (_doc is null) yield break;
        long rows = _doc.RowCount, n = 0;
        foreach (var (a, b) in _sel.Ranges)
            for (long row = _doc.RowAtOrAfterLine(a); row < rows; row++)
            {
                long line = _doc.RowToLine(row);
                if (line > b) break;
                if (n++ >= cap) yield break;
                yield return line;
            }
    }

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
        _sel.SetSingle(LineAt(row));
        RevealRow(row);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    public void CopySelection(bool withLineNumbers)
    {
        if (_doc is null) return;
        if (SelectedText is { } part)
        {
            string one = withLineNumbers ? $"{_charLine + 1}\t{part}" : part;
            try { Clipboard.SetText(one); } catch { /* clipboard busy */ }
            return;
        }
        if (_sel.IsEmpty) return;
        var sb = new StringBuilder();
        foreach (long line in SelectedLines(CopyLineCap))
        {
            if (withLineNumbers) sb.Append(line + 1).Append('\t');
            sb.AppendLine(_doc.GetLineText(line));
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
        _sel.SetSingle(LineAt(row));
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
                if (_g._sel.Contains(_g.LineAt(Row))) s |= AccessibleStates.Selected;
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

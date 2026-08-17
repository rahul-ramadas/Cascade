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

    /// <summary>How much text one copy will gather. A cap in LINES is barely a cap at all: the same two
    /// million lines is a couple of hundred megabytes of a short-line log and tens of gigabytes of one
    /// carrying a JSON payload per line. The cost is proportional to CHARACTERS, so that is what is
    /// budgeted - and the clipboard holds a second copy of whatever it is given, so this is doubled in
    /// practice. Far more than anything will paste it, and bounded whatever the file looks like.
    /// A field so a check can lower it instead of building a 64 MB fixture.</summary>
    internal int CopyCharCap = 32 * 1024 * 1024;

    private SlimScrollBar _hbar = null!;
    private SlimScrollBar _vbar = null!;
    private MiniMapControl? _map;
    private readonly LineSelection _sel = new();
    private readonly TemplateMatch _match = new();
    private readonly LineProjection _projection = new();

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
    // The same eight as GDI knows them. Drawing a row goes straight to GDI (see GdiCanvas), which wants a
    // handle rather than a Font, and making one per call would be an object created and destroyed per row.
    private readonly IntPtr[] _faces = new IntPtr[8];
    private readonly GdiCanvas _canvas = new();
    private Font FontRegular => _fonts[0];
    private Font FontBold => _fonts[1];
    private Font FontItalic => _fonts[2];
    private FontFamily? _fontFamily;
    /// <summary>How tall the text on a chip is drawn. Measured when the fonts are built rather than on
    /// every paint - laying the strip out asks for it once per chip.</summary>
    private int _chipTextHeight = 12;
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
    private int _tipChip = -1;
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

    /// <summary>Raised when a copy took less than was selected, with how many lines it took and how many
    /// were asked for. Saying nothing would leave the reader with a quietly incomplete clipboard.</summary>
    public event Action<long, long>? CopyTruncated;

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
        _vbar.Scrolled += v => { ClearViewAnchor(); _firstRow = v; ShowAtScreenRate(); };
        _hbar.Scrolled += v => { _hScroll = (int)v; ShowAtScreenRate(); };
        // The map is a child, so it is not repainted by the grid repainting - and everything it draws is a
        // picture of the grid's own state. One hook here rather than a call beside every Invalidate() in the
        // file, because one of those would eventually be forgotten.
        Invalidated += (_, _) => ViewMoved(onScreen: true);
        _tipTimer.Tick += (_, _) => ShowTipNow();
        _catchUp.Tick += (_, _) =>
        {
            // Nothing has been missed since the last tick, so the hand has stopped and this can too. The
            // timer is left running while a drag is in flight rather than being restarted per report:
            // starting one destroys and recreates a window, which at a few hundred reports a second costs
            // more than the frames it is there to catch.
            if (!_missedFrame) { _catchUp.Stop(); return; }
            ShowAtScreenRate();
        };
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

    /// <summary>Which fields are being shown, and in what order, as one number - so a selection made against
    /// one arrangement can be told apart from the same numbers under another.</summary>
    private int FieldsShape()
    {
        if (_doc is null || !_doc.Columns.Active) return 0;
        int shape = _doc.Columns.Layout == FieldLayout.Inline ? 17 : 31;
        foreach (var column in _doc.Columns.Columns)
            shape = shape * 31 + (column.Visible ? column.Source + 1 : -(column.Source + 1));
        return shape;
    }

    /// <summary>The arrangement the part-of-a-line selection was made against.</summary>
    private int _charShape;

    /// <summary>Whether the log is being shown split into cells rather than as whole lines. Only the
    /// Columns layout does that; Inline keeps every row a line and simply leaves parts out of it.</summary>
    private bool ColumnsOn => _doc is not null && _doc.Columns.Active && _doc.Columns.Layout == FieldLayout.Columns;

    /// <summary>Whether rows are being shortened in place - still lines, with the hidden parts left out.</summary>
    private bool InlineOn => _doc is not null && _doc.Columns.Active && _doc.Columns.Layout == FieldLayout.Inline;

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
        if (ColumnsOn) return raw;
        return Expand(InlineOn ? Project(raw).Text : raw);
    }

    /// <summary>The shared projection, rebuilt for whichever line is being asked about. Reused rather than
    /// made afresh because the paint asks for one per row per frame; the caller must read what it needs
    /// before asking about another line.</summary>
    private LineProjection Project(string line)
    {
        _doc!.Columns.Compiled.Match(line, _match);
        _projection.Build(line, _doc.Columns, _match);
        return _projection;
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
        var eval = _doc.ColouringSnapshot().Evaluate(text, _doc.RowToLine(row));
        return SelectFont(eval.ColorFilter is not null ? StyleResolver.Resolve(eval.ColorFilter, defaults) : defaults);
    }

    private int PrefixWidth(ReadOnlySpan<char> text, int count, Font font)
        => MeasureWidth(text[..Math.Clamp(count, 0, text.Length)], font);

    /// <summary>Width of a stretch of text. Takes a span because wrapping binary-searches for the break,
    /// and a substring per probe would churn the heap on every frame.</summary>
    private int MeasureWidth(ReadOnlySpan<char> text, Font font)
        => text.IsEmpty ? 0 : DrawnWidth(text, font, CharWidthOf(font));

    /// <summary>The width of one character in a face, when every character in it is the same width. Found
    /// by identity rather than measured: there are eight faces and this is asked once per probe of a
    /// binary search.</summary>
    private int CharWidthOf(Font font)
    {
        if (!_monospaced) return 0;
        for (int i = 0; i < _fonts.Length; i++)
            if (ReferenceEquals(_fonts[i], font)) return _charWidths[i];
        return 0;
    }

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

    /// <summary>Marks every occurrence on show and re-draws the text over it in the ordinary text colour.
    /// Without the second part a hit on a selected row would be white on orange - and the row the search
    /// just landed on is always selected.</summary>
    private void DrawHighlightText(GdiCanvas ink, string text, int from, int to, Rectangle strip,
                                   Font font, int fontIndex)
    {
        foreach (var (at, len, colour) in _highlights)
        {
            int a = Math.Max(at, from), b = Math.Min(at + len, to);
            if (b <= a) continue;
            int x0 = SegmentX(text, from, a, strip.Left, font);
            int x1 = SegmentX(text, from, b, strip.Left, font);
            var box = Rectangle.Intersect(new Rectangle(x0, strip.Top, Math.Max(1, x1 - x0), _rowHeight), strip);
            ink.Fill(box, colour);
            var part = text.AsSpan(a, b - a);
            ink.TextOver(part, x0, strip.Top, strip, _settings.Foreground, font, _faces[fontIndex],
                         Plain(part, fontIndex));
        }
    }

    /// <summary>Whether a stretch of text is the plain printable ASCII that can go through the shortest
    /// call GDI has, in a face where every character is the same width.</summary>
    private bool Plain(ReadOnlySpan<char> text, int fontIndex)
        => !_longWay && CharWidthOf(fontIndex) > 0 && text.IndexOfAnyExceptInRange(' ', '~') < 0;

    private bool _longWay;

    /// <summary>Test seam: put every piece of text back through the general text layout, as this did before
    /// the direct path existed, so a check can prove the two draw the same picture.</summary>
    [System.ComponentModel.DefaultValue(false)]
    internal bool DrawTextTheLongWayForTesting
    {
        get => _longWay;
        set { _longWay = value; Invalidate(); }
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
        if (StandInLine is var stand && stand >= 0)
        {
            long row = _doc.RowForLine(stand);
            if (row >= 0) into.Add((row, row + 1));
            return;
        }
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
        ShowAtScreenRate();
    }

    // ---- how often a dragged view is actually put on the screen ----

    private readonly System.Windows.Forms.Timer _catchUp = new() { Interval = 15 };
    private long _shownAt;
    private long _screenAskedAt;
    private int _screenHz = 60;
    private bool _missedFrame;

    /// <summary>
    /// Puts the view on screen after a scroll, but never more often than the screen can show a new frame.
    ///
    /// <para>A drag on the scrollbar or the map reports as fast as the mouse does - a thousand times a
    /// second on a modern one - and every report used to repaint the whole window there and then. Painting
    /// faster than the screen refreshes cannot be seen by anyone: the compositor shows the newest frame at
    /// the next refresh and throws the rest away. Measured on a 1000 Hz mouse, that was 287 repaints a
    /// second and two thirds of a processor spent so that sixty of them could be seen.</para>
    ///
    /// <para>What is skipped is a <b>frame</b>, never a position: the view has already moved, and the next
    /// report - or the timer behind it, for the report that turns out to be the last - draws wherever the
    /// hand has got to by then. So a whole page still lands on screen each time, which is what a drag is
    /// supposed to look like, and the one that lands is the one the pointer is actually pointing at.</para>
    /// </summary>
    private void ShowAtScreenRate()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _shownAt >= System.Diagnostics.Stopwatch.Frequency / Math.Max(1, ScreenRate()))
        {
            Invalidate();
            Update();
            return;
        }
        // Too soon to be seen. The view has moved all the same, so something has to draw it in case this
        // report is the last one.
        _missedFrame = true;
        ViewMoved(onScreen: false);
        if (!_catchUp.Enabled) _catchUp.Start();
    }

    /// <summary>What the two strips beside the text are owed when the view moves. They are children, so a
    /// repaint of the text does not reach them, and they have to hear about a move even on the frames that
    /// are not drawn - the map remembers where the view was last put, and would otherwise decide the view
    /// had wandered off on its own and re-centre itself under a hand that was dragging it.
    /// <para>The map is redrawn only on the frames that are: it is a picture of the same view, and one
    /// nobody can see is worth no more than a page of text nobody can see. The scrollbar draws itself on
    /// every report of a drag, and should - the thumb is the thing the hand is watching.</para></summary>
    private void ViewMoved(bool onScreen)
    {
        _map?.SyncToGrid(onScreen);
        if (onScreen) _vbar?.Invalidate();
    }

    /// <summary>How many times a second the screen this window is on can show something new. Asked of the
    /// monitor rather than assumed, because 60, 120 and 144 are all ordinary now - and asked again from time
    /// to time, because a window can be dragged onto a different screen.</summary>
    private int ScreenRate()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_screenAskedAt != 0 && now - _screenAskedAt < System.Diagnostics.Stopwatch.Frequency) return _screenHz;
        _screenAskedAt = now;
        try
        {
            IntPtr dc = CreateDC(Screen.FromControl(this).DeviceName, null, null, IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                int hz = GetDeviceCaps(dc, VerticalRefresh);
                DeleteDC(dc);
                if (hz > 1) _screenHz = hz;
            }
        }
        catch (InvalidOperationException) { /* no screen to ask; whatever it said last stands */ }
        return _screenHz;
    }

    private const int VerticalRefresh = 116;

    [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateDC(string driver, string? device, string? port, IntPtr mode);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr dc, int index);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

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
    public void SetViewAnchor(ViewAnchor anchor)
    {
        _anchorLine = anchor.Line;
        _anchorCaretLine = anchor.CaretLine;
        _anchorOffset = Math.Clamp(anchor.Offset, 0, Math.Max(0, EffectiveVisibleRows - 1));
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

    /// <summary>Keeps the caret on its original line as rows shift. The selection is left alone: it is held
    /// in lines, so hiding some of it is a question of what is drawn, not of what is chosen.</summary>
    private void PinCaretToAnchor()
    {
        if (_doc is null || _anchorCaretLine < 0) return;
        long rows = _doc.RowCount;
        long caret = ResolveRow(_anchorCaretLine);
        if (caret < 0 || rows == 0) return;
        _caretRow = Math.Clamp(caret, 0, rows - 1);
    }

    /// <summary>Whether any chosen line is on screen. Two rank lookups a range, and it stops at the first
    /// one that is, so the usual single range costs two.</summary>
    private bool AnyChosenVisible
    {
        get
        {
            if (_doc is null) return false;
            foreach (var (a, b) in _sel.Ranges)
                if (_doc.RowAtOrAfterLine(b + 1) > _doc.RowAtOrAfterLine(a)) return true;
            return false;
        }
    }

    /// <summary>The line standing in for a selection the filters have hidden every line of, or -1.
    /// <para>Something has to be highlighted or the reader loses their place entirely, so the caret's line
    /// is shown in its stead - the caret has already moved to the nearest line still on show. It is only
    /// ever DRAWN as selected: what was chosen is untouched, so putting the lines back - undoing the filter,
    /// pressing Ctrl+H again - shows the original selection again rather than this.</para></summary>
    private long StandInLine => _doc is null || _sel.IsEmpty || AnyChosenVisible ? -1 : CaretLine;

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
        ReleaseFaces();
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
        for (int i = 0; i < _faces.Length; i++) _faces[i] = _fonts[i].ToHfont();
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
        BuildChipFont();
        _naturalKey = null;   // the widths the content asks for are measured in this font
        Invalidate();
    }

    /// <summary>
    /// The face the chips above the log are labelled in: the WINDOW's own font, the one the menu bar and
    /// every other piece of chrome uses.
    ///
    /// <para>The log's face is the wrong tool. A chip is a control, not log text - it is read at whatever
    /// size the rest of the window is read at, and it does not shrink because someone chose a small fixed
    /// pitch for their log or zoomed out. Sized from the log it came out a few pixels tall and unreadable,
    /// and squeezed into a row of it, cropped.</para>
    /// </summary>
    private Font ChipFont => Font;

    private void BuildChipFont()
        => _chipTextHeight = TextRenderer.MeasureText("Xg", ChipFont, new Size(int.MaxValue, int.MaxValue),
               TextFormatFlags.NoPadding).Height;

    /// <summary>Recomputes scrollbar ranges from the document and repaints. Call (on the UI thread)
    /// whenever counts change or the view mode/filters change.</summary>
    public void RefreshView()
    {
        long rows = _doc?.RowCount ?? 0;
        int visible = EffectiveVisibleRows;

        // A spec built in code, or read from a file written before a column said which part it shows, may
        // not have settled that yet. The table layout does it while measuring its widths; inline has no such
        // pass, and a column that shows no part is simply left out - a row of nothing at all.
        if (_doc is not null && _doc.Columns.Enabled) _doc.Columns.NormalizeSources();

        // A rename box belongs to a header that may no longer be there.
        if (_renameBox is not null && HeaderHeight == 0) EndRename(commit: false);
        DropSelectionIfArrangementChanged();

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

    /// <summary>Where the text starts: below anything sitting above it and below the header strip.</summary>
    private int TextTop => TopInset + HeaderHeight;

    /// <summary>How many rows the strip above the text takes. Columns puts a draggable header there and one
    /// row is its own line height, so one row is right. Inline puts CHIPS there, which are labelled in the
    /// window's font rather than the log's - so at a small log font they need more than one of its rows.
    ///
    /// <para>A whole number of rows either way, and not a pixel more: everything that keeps the line under
    /// the reader still while this appears and disappears works in rows, and half a row of strip would move
    /// the whole log by half a line with no way to scroll it back.</para></summary>
    internal int HeaderRows
    {
        get
        {
            if (!(_doc?.Columns.Active ?? false)) return 0;
            if (_doc!.Columns.Layout != FieldLayout.Inline) return 1;
            int wanted = ChipHeight + 2 * LogicalToDeviceUnits(2);
            return Math.Max(1, (wanted + _rowHeight - 1) / Math.Max(1, _rowHeight));
        }
    }

    /// <summary>The strip above the text. Columns puts a draggable header there; Inline puts the chips that
    /// show which parts are being kept.</summary>
    private int HeaderHeight => HeaderRows * _rowHeight;

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

    /// <summary>Whether lines are broken to fit the width. Not offered while they are laid out in COLUMNS:
    /// wrapping inside a cell is a different feature, and the menu says so by greying out. Inline is only a
    /// shorter line, so it wraps like any other - which the menu offers, and had better mean.</summary>
    internal bool Wrapping => _settings.WordWrap && !ColumnsOn;

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

    /// <summary>The top row of the last frame actually drawn, which is not the same thing as the row the
    /// view is on: a drag moves the view faster than the screen can show it.</summary>
    internal long FirstPaintedRowForTesting => _layout.Count > 0 ? _layout[0].Row : -1;

    /// <summary>How many times the view has actually repainted. A picture of it cannot answer that - drawing
    /// a control to a bitmap paints it whether or not anything asked it to.</summary>
    internal int PaintsForTesting => _paints;

    /// <summary>Raised inside a frame, after its rows have been resolved and before any of them is drawn.</summary>
    internal Action? AfterWindowForTesting;

    internal long CharOriginForTesting => _charOriginRow;

    /// <summary>Which file line the part-of-a-line selection is on, or -1. The whole point of the selection
    /// is that this answer does not change when the filters do.</summary>
    internal long CharSelectionLineForTesting => _charLine;

    /// <summary>Whether a file line is selected, whether or not the view is currently showing it.</summary>
    internal bool IsLineSelectedForTesting(long line) => _sel.Contains(line);

    /// <summary>Whether a line is DRAWN selected - the chosen lines, or the stand-in when every chosen line
    /// is hidden. The two differ exactly while the filters are covering the whole selection.</summary>
    internal bool IsLineShownSelectedForTesting(long line) => _sel.Contains(line) || line == StandInLine;

    internal long StandInLineForTesting => StandInLine;

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

        // Which filters answer for a row is fixed here, once, and BEFORE the rows are resolved: the pass
        // can finish at any moment, and taking it first means a frame can only ever be too generous - rows
        // resolved afterwards are ones the new filters already show, and answer for themselves.
        var colouring = _doc.ColouringSnapshot();

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

        // The rows are settled and nothing has been drawn yet: a check hooks in here to make the world move
        // exactly where it used to be able to move under a frame.
        AfterWindowForTesting?.Invoke();

        var defaults = new ResolvedStyle(ToRgb(_settings.Foreground), ToRgb(_settings.Background), false, false);
        // What is picked out of a line, once for the frame rather than once per row: every row asks whether
        // it carries the same text, and answering it decodes the line the selection came from.
        string? selected = SelectedText;
        long standIn = StandInLine;

        bool columns = ColumnsOn;
        bool inline = InlineOn;
        int runningMaxWidth = 0;

        if (columns) { EnsureColumnLayout(); DrawColumnHeader(g, gutter, contentW); }
        else if (inline) DrawFieldChips(g);

        _layout.Clear();
        int atY = TextTop;
        int bottom = ClientSize.Height - BottomInset;
        int caretTop = -1, caretHeight = 0;
        // The whole row loop draws on the device context itself, borrowed once. Nothing may touch the
        // Graphics until it is given back - and nothing here wants to: filling a row as part of drawing its
        // text is the cheapest way there is to put a line on screen (see GdiCanvas). What GDI+ is still
        // needed for - the caret's translucent outline - is done afterwards, off the layout it left behind.
        var ink = _canvas;
        ink.Borrow(g);
        try
        {
            for (int i = 0; i < visible; i++)
            {
                long row = _firstRow + i;
                if (row >= rows || i >= windowCount) break;
                if (atY >= bottom) break;
                int y = atY;
                long line = _window[i];
                string text = _doc.GetLineText(line);
                var eval = colouring.Evaluate(text, line);

                ResolvedStyle style = eval.ColorFilter is not null
                    ? StyleResolver.Resolve(eval.ColorFilter, defaults)
                    : defaults;

                // Both of these are asked of the LINE, not of the row it landed on this frame: the filters move
                // every row about, and a highlight left on a row would end up over text nobody picked out.
                bool charSel = HasCharSelection && line == _charLine;
                bool selectedRow = !charSel && (_sel.Contains(line) || line == standIn);
                bool dim = !_doc.FilteredMode && !eval.Shown;

                Color back = selectedRow ? _settings.SelectionBack : ToColor(style.Background);
                Color fore = selectedRow ? _settings.SelectionFore : (dim ? _settings.DimForeground : ToColor(style.Foreground));

                int fontIndex = FontIndex(style);
                Font font = _fonts[fontIndex];
                int charWidth = CharWidthOf(fontIndex);
                string shown = columns ? text : Expand(inline ? Project(text).Text : text);
                int segments = columns ? 1 : WrapInto(shown, contentW, font, _segments);
                int rowHeight = segments * _rowHeight;
                _layout.Add((row, y, rowHeight, segments));

                DrawGutters(ink, line, y, rowHeight, selectedRow);

                var contentRect = new Rectangle(gutter, y, contentW, rowHeight);
                if (columns)
                {
                    ink.Fill(contentRect, back);
                    using (ink.Clip(contentRect))
                        DrawColumns(ink, text, row, gutter, y, fore, back, fontIndex, charSel, selected);
                }
                else
                {
                    CollectHighlights(shown, row == _caretRow, charSel, selected);
                    for (int s = 0; s < segments; s++)
                    {
                        int from = _segments[s];
                        int to = s + 1 < _segments.Count ? _segments[s + 1] : shown.Length;
                        int sy = y + s * _rowHeight;
                        var strip = new Rectangle(gutter, sy, contentW, _rowHeight);
                        runningMaxWidth = Math.Max(runningMaxWidth,
                            DrawSegment(ink, shown, from, to, strip, back, fore, font, fontIndex, charWidth));
                        DrawHighlightText(ink, shown, from, to, strip, font, fontIndex);
                        if (charSel) DrawCharSelection(ink, shown, from, to, strip, font, fontIndex);
                    }
                }

                if (_doc.IsLineTruncated(line))
                    ink.TextIn(" […]", new Rectangle(ClientSize.Width - RightGutterWidth - 40, y, 40, _rowHeight),
                        Color.Gray, FontItalic, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                if (row == _caretRow && Focused) { caretTop = y; caretHeight = rowHeight; }

                atY += rowHeight;
            }

            // Only the strip no row reached needs the background. Clearing the whole view first and then
            // painting a row over every part of it wrote every pixel of the window twice.
            if (atY < bottom)
                ink.Fill(new Rectangle(0, atY, ClientSize.Width - RightGutterWidth, bottom - atY), _settings.Background);
        }
        finally { ink.Release(); }

        if (caretTop >= 0)
            using (var pen = new Pen(Color.FromArgb(120, _settings.SelectionBack)))
                g.DrawRectangle(pen, 0, caretTop, ClientSize.Width - RightGutterWidth - 1, caretHeight - 1);

        DrawFocusBar(g);

        _shownAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _missedFrame = false;

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
            // The full row height, centred: in a box two pixels shorter the descenders of a proportional
            // face were shaved off the bottom of every name that had one.
            TextRenderer.DrawText(g, def.Name, FontBold, new Rectangle(x + 3, top, w - 6, _rowHeight),
                Color.FromArgb(80, 80, 80), CellFlags(ColumnAlign.Left, x, w, gutter, right) | TextFormatFlags.VerticalCenter);
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

    // ---- the chip strip: how parts are hidden and carried about while the layout is Inline ----

    /// <summary>Room either side of a chip's label, and between one chip and the next.</summary>
    private int ChipPad => LogicalToDeviceUnits(7);
    private int ChipGap => LogicalToDeviceUnits(5);

    /// <summary>How tall a chip is: what its label needs, and a little either side of it. Sized from the
    /// label rather than from a log row, because the label is not log text.</summary>
    private int ChipHeight => _chipTextHeight + 2 * LogicalToDeviceUnits(3);

    /// <summary>The colour patch on a chip, a little smaller than the text beside it.</summary>
    private int ChipSwatch => Math.Max(LogicalToDeviceUnits(6), _chipTextHeight - LogicalToDeviceUnits(4));

    /// <summary>Where every chip sits, in display order, and - when they do not all fit - where the button
    /// that opens the rest sits. Laid out from the left of the content area and NOT scrolled with the text:
    /// the chips say what the layout is doing, so they stay put while the log moves under them.</summary>
    private List<(int Column, Rectangle Rect)> ChipRects()
    {
        var result = new List<(int, Rectangle)>();
        if (_doc is null) return result;

        int right = ClientSize.Width - RightGutterWidth;
        // Level with the text below, not a gap further in: the first chip is what the eye lines the strip up
        // by, and the header in the other layout starts exactly there.
        int x = GutterWidth();
        int height = ChipHeight;
        int top = TopInset + Math.Max(0, (HeaderHeight - height) / 2);
        var spec = _doc.Columns;
        int room = TextRenderer.MeasureText("\u00bb 00", ChipFont, new Size(int.MaxValue, height),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 2 * ChipPad + ChipGap;

        for (int i = 0; i < spec.Columns.Count; i++)
        {
            string name = spec.Columns[i].Name;
            int text = TextRenderer.MeasureText(name, ChipFont, new Size(int.MaxValue, height),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            int width = ChipPad + ChipSwatch + LogicalToDeviceUnits(5) + text + ChipPad;

            // No one chip may eat the strip: a very long name is drawn with an ellipsis rather than pushing
            // every field after it out of reach.
            int most = Math.Max(LogicalToDeviceUnits(60), (right - GutterWidth()) / 3);
            width = Math.Min(width, most);

            // Stop while there is still room for the button that reaches the rest, rather than clipping the
            // last chip and leaving the fields beyond it with no way to be got at.
            bool last = i == spec.Columns.Count - 1;
            if (x + width > right - (last ? 0 : room))
            {
                _chipsOverflowing = spec.Columns.Count - result.Count;
                int at = Math.Min(x, Math.Max(GutterWidth(), right - room));
                _chipOverflowRect = new Rectangle(at, top, room - ChipGap, height);
                return result;
            }

            result.Add((i, new Rectangle(x, top, width, height)));
            x += width + ChipGap;
        }

        _chipsOverflowing = 0;
        _chipOverflowRect = Rectangle.Empty;
        return result;
    }

    private int _chipsOverflowing;
    private Rectangle _chipOverflowRect;

    private void DrawFieldChips(Graphics g)
    {
        int top = TopInset;
        var strip = new Rectangle(0, top, ClientSize.Width - RightGutterWidth, HeaderHeight);
        using (var brush = new SolidBrush(_settings.GutterBack)) g.FillRectangle(brush, strip);
        using (var pen = new Pen(Color.FromArgb(210, 210, 210)))
            g.DrawLine(pen, 0, strip.Bottom - 1, strip.Width, strip.Bottom - 1);

        // Disposed once the strip is drawn: Graphics.Clip hands back a Region around a native handle, and
        // one of those a frame is one the finalizer has to clean up.
        using var clip = g.Clip;
        g.SetClip(strip);

        var spec = _doc!.Columns;
        foreach (var (index, rect) in ChipRects())
        {
            if (rect.Left > strip.Right) break;
            var def = spec.Columns[index];
            bool carrying = _colGesture == ColumnGesture.Reorder && index == _colIndex && _colMoved;
            bool under = index == _chipUnderPointer && _colGesture == ColumnGesture.None;

            // A chip has to read as something to press, not as a key to the colours: it lifts under the
            // pointer, and its edge darkens, which is what says "this does something".
            using (var brush = new SolidBrush(def.Visible ? SystemColors.Window : _settings.GutterBack))
                g.FillRectangle(brush, rect);
            if (under)
                using (var brush = new SolidBrush(Color.FromArgb(40, _settings.SelectionBack)))
                    g.FillRectangle(brush, rect);
            if (carrying)
                using (var brush = new SolidBrush(Color.FromArgb(60, _settings.SelectionBack)))
                    g.FillRectangle(brush, rect);
            using (var pen = new Pen(under ? Color.FromArgb(110, 110, 110)
                                   : def.Visible ? Color.FromArgb(170, 170, 170) : Color.FromArgb(205, 205, 205)))
                g.DrawRectangle(pen, rect);

            var swatch = new Rectangle(rect.Left + ChipPad, rect.Top + (rect.Height - ChipSwatch) / 2, ChipSwatch, ChipSwatch);
            using (var brush = new SolidBrush(def.Visible ? ColumnsPreview.BandOf(def.Source) : Color.FromArgb(215, 215, 215)))
                g.FillRectangle(brush, swatch);
            using (var pen = new Pen(Color.FromArgb(160, 160, 160))) g.DrawRectangle(pen, swatch);

            int textLeft = swatch.Right + LogicalToDeviceUnits(5);
            TextRenderer.DrawText(g, def.Name, ChipFont,
                new Rectangle(textLeft, rect.Top, rect.Right - ChipPad - textLeft, rect.Height),
                def.Visible ? Color.FromArgb(60, 60, 60) : Color.FromArgb(150, 150, 150),
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (!def.Visible)
                using (var pen = new Pen(Color.FromArgb(150, 150, 150)))
                    g.DrawLine(pen, textLeft, rect.Top + rect.Height / 2, rect.Right - ChipPad, rect.Top + rect.Height / 2);
        }

        // Whatever did not fit is still reachable: this opens the same tick list the strip's own menu does.
        if (_chipsOverflowing > 0 && !_chipOverflowRect.IsEmpty)
        {
            bool under = _chipUnderPointer == OverflowChip;
            using (var brush = new SolidBrush(under ? SystemColors.ControlLight : _settings.GutterBack))
                g.FillRectangle(brush, _chipOverflowRect);
            using (var pen = new Pen(Color.FromArgb(under ? 110 : 170, under ? 110 : 170, under ? 110 : 170)))
                g.DrawRectangle(pen, _chipOverflowRect);
            TextRenderer.DrawText(g, $"\u00bb {_chipsOverflowing}", ChipFont, _chipOverflowRect,
                Color.FromArgb(60, 60, 60),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        g.Clip = clip;
    }

    /// <summary>The pretend index of the button that reaches the chips that did not fit.</summary>
    private const int OverflowChip = -2;

    private int _chipUnderPointer = -1;

    /// <summary>The chip under a point, <see cref="OverflowChip"/> for the button that reaches the rest,
    /// or -1.</summary>
    private int ChipAt(int x, int y)
    {
        foreach (var (index, rect) in ChipRects())
            if (rect.Contains(x, y)) return index;
        return _chipsOverflowing > 0 && _chipOverflowRect.Contains(x, y) ? OverflowChip : -1;
    }

    /// <summary>The gesture the pointer starts on the chip strip. A click puts a part away or brings it
    /// back; a drag carries it to another place in the row; a double-click renames it, as it does on the
    /// header - and the toggle the first click of the pair had already made is taken back, so that renaming
    /// never leaves a field hidden behind the box being typed in.</summary>
    private bool HandleChipMouseDown(MouseEventArgs e, int clicks)
    {
        if (e.Button == MouseButtons.Right) { ShowColumnMenu(e.Location); return true; }
        if (e.Button != MouseButtons.Left) return true;

        int chip = ChipAt(e.X, e.Y);
        if (chip == OverflowChip) { ShowColumnMenu(e.Location); return true; }
        if (chip < 0) return true;

        if (clicks >= 2)
        {
            if (_toggledChip == chip) { _doc!.Columns.Columns[chip].Visible = !_doc.Columns.Columns[chip].Visible; ColumnsEdited(); }
            _toggledChip = -1;
            HideTip();
            BeginRename(chip);
            return true;
        }

        _colGesture = ColumnGesture.Reorder;
        _colIndex = chip;
        _colGrabX = e.X;
        _colMoved = false;
        Capture = true;
        return true;
    }

    /// <summary>Which chip the last click put away or brought back, so a second click that turns out to be
    /// half of a double-click can undo it.</summary>
    private int _toggledChip = -1;

    private void HandleChipMouseMove(MouseEventArgs e)
    {
        if (_colGesture != ColumnGesture.Reorder)
        {
            int over = InColumnHeader(e.Y) ? ChipAt(e.X, e.Y) : -1;
            if (over != _chipUnderPointer) { _chipUnderPointer = over; Invalidate(); }
            SetCursorTo(over >= 0 ? Cursors.Hand : Cursors.Default);
            return;
        }
        if (!_colMoved && Math.Abs(e.X - _colGrabX) < SystemInformation.DragSize.Width) return;
        // Once the chip is actually travelling, whatever the tip was saying about where it sits is out of
        // date with every pixel. A tip is for a chip standing still.
        if (!_colMoved) HideTip();
        _colMoved = true;

        var chips = ChipRects();
        int from = chips.FindIndex(c => c.Column == _colIndex);
        if (from < 0) return;
        var widths = chips.Select(c => c.Rect.Width + ChipGap).ToList();
        int to = ColumnLayout.DropTarget(widths, from, e.X - GutterWidth());
        if (to != from && to >= 0 && to < _doc!.Columns.Columns.Count)
        {
            var moved = _doc.Columns.Columns[_colIndex];
            _doc.Columns.Columns.RemoveAt(_colIndex);
            _doc.Columns.Columns.Insert(to, moved);
            _colIndex = to;
            ColumnsEdited();
        }
        else Invalidate();
    }

    /// <summary>A press that never turned into a drag is a click, and a click on a chip is what puts that
    /// part away or brings it back.</summary>
    private void EndChipGesture()
    {
        if (_colGesture != ColumnGesture.Reorder) return;
        bool moved = _colMoved;
        int chip = _colIndex;
        _colGesture = ColumnGesture.None;
        _colIndex = -1;
        _colMoved = false;
        _toggledChip = -1;
        Capture = false;
        SetCursorTo(Cursors.Default);

        if (!moved && chip >= 0 && chip < _doc!.Columns.Columns.Count)
        {
            var def = _doc.Columns.Columns[chip];
            // The last one standing cannot be put away: a row with nothing in it says nothing, and there
            // would be no chip left to bring anything back with.
            if (def.Visible && _doc.Columns.Columns.Count(c => c.Visible) <= 1) { Invalidate(); return; }
            def.Visible = !def.Visible;
            _toggledChip = chip;
            RefreshChipTip(chip);
        }
        ColumnsEdited();
    }

    /// <summary>Draws one row's cells. Only the ones on screen: with a line split into dozens of fields
    /// most of them are off the side, and a cell costs a text draw whether or not anyone can see it.
    /// It does NOT report a content width - the row is as wide as the columns are, which the caller reads
    /// once from <see cref="TotalColumnsWidth"/> rather than once per row.</summary>
    private void DrawColumns(GdiCanvas ink, string text, long row, int gutter, int y, Color fore, Color back,
                             int fontIndex, bool charSel, string? selected)
    {
        var template = _doc!.Columns.Compiled;
        var font = _fonts[fontIndex];
        CollectHighlights(text, row == _caretRow, charSel, selected);

        // A line the template does not fit is shown whole, across the row. Columns can shorten a line;
        // they can never hide one, and a screenful of empty cells says nothing about why.
        if (!template.Match(text, _match))
        {
            int from = 0, to = text.Length;
            var strip = new Rectangle(gutter, y, ContentWidth, _rowHeight);
            ink.TextOver(text, gutter - _hScroll, y, strip, fore, font, _faces[fontIndex],
                         Plain(text, fontIndex));
            DrawHighlightText(ink, text, from, to, strip, font, fontIndex);
            if (charSel) DrawCharSelection(ink, text, from, to, strip, font, fontIndex);
            return;
        }

        bool marks = _highlights.Count > 0 || charSel;
        int x = gutter - _hScroll;
        int right = gutter + ContentWidth;
        var spec = _doc.Columns;
        for (int i = 0; i < spec.Columns.Count; i++)
        {
            var def = spec.Columns[i];
            if (!def.Visible) continue;
            int w = _colWidths[i];
            if (x >= right) break;
            if (x + w <= gutter) { x += w; continue; }
            var cell = new Rectangle(x + CellInset, y, w - 2 * CellInset, _rowHeight);
            var span = CellText(text, def, _match);
            if (!marks)
            {
                DrawCell(ink, span, cell, fore, back, font, fontIndex, def.Align, CellFlags(def.Align, x, w, gutter, right));
            }
            else
            {
                var (from, to) = CellRange(def, _match);
                int originX = CellTextOrigin(cell.Left, cell.Width, span, font, def.Align);
                FillCellHighlights(ink, text, from, to, originX, cell, font);
                ink.TextIn(span, cell, fore, font, CellFlags(def.Align, x, w, gutter, right));
                DrawCellHighlightText(ink, text, from, to, originX, cell, font);
                if (charSel && i == _charColumn) DrawCellCharSelection(ink, text, from, to, originX, cell, font);
            }
            x += w;
        }
    }

    /// <summary>Draws one cell's text. Plain ASCII in a fixed-pitch face that fits its box goes by the same
    /// short road a whole line does - one call that lays the background down with the text - and is placed
    /// by arithmetic, which is exactly where the layout would have put it. Anything else (a proportional
    /// face, a script that needs shaping, or text too long for its box and so wanting the ellipsis) goes
    /// through the layout, over the fill the row has already had.</summary>
    private void DrawCell(GdiCanvas ink, ReadOnlySpan<char> span, Rectangle cell, Color fore, Color back,
                          Font font, int fontIndex, ColumnAlign align, TextFormatFlags flags)
    {
        int charWidth = CharWidthOf(fontIndex);
        int width = span.Length * charWidth;
        if (!Plain(span, fontIndex) || width > cell.Width)
        {
            ink.TextIn(span, cell, fore, font, flags);
            return;
        }
        int x = align switch
        {
            ColumnAlign.Right => cell.Right - width,
            ColumnAlign.Center => cell.Left + (cell.Width - width) / 2,
            _ => cell.Left
        };
        ink.Text(span, x, cell.Top, cell, fore, back, font, _faces[fontIndex], plain: true);
    }

    /// <summary>Where a character of a cell's text sits on screen.</summary>
    private int CellX(string line, int from, int index, int originX, Font font)
        => originX + MeasureWidth(line.AsSpan(from, Math.Max(0, index - from)), font);

    /// <summary>Fills whatever of the marked ranges falls inside this cell. Clamped to the cell's own box:
    /// a cell whose text is wider than it is would otherwise paint its marks over the column beside it.</summary>
    private void FillCellHighlights(GdiCanvas ink, string line, int from, int to, int originX, Rectangle cell, Font font)
    {
        foreach (var (at, len, colour) in _highlights)
        {
            int a = Math.Max(at, from), b = Math.Min(at + len, to);
            if (b <= a) continue;
            var rect = Rectangle.Intersect(cell, Span(a, b));
            if (rect.Width <= 0) continue;
            ink.Fill(rect, colour);
        }

        Rectangle Span(int a, int b)
        {
            int x0 = CellX(line, from, a, originX, font), x1 = CellX(line, from, b, originX, font);
            return new Rectangle(x0, cell.Top, Math.Max(1, x1 - x0), cell.Height);
        }
    }

    /// <summary>Re-draws marked text over its own fill in the ordinary colour, as the whole-line path does -
    /// a hit on a selected row would otherwise be white on orange.</summary>
    private void DrawCellHighlightText(GdiCanvas ink, string line, int from, int to, int originX, Rectangle cell, Font font)
    {
        foreach (var (at, len, _) in _highlights)
        {
            int a = Math.Max(at, from), b = Math.Min(at + len, to);
            if (b <= a) continue;
            DrawInCell(ink, line.AsSpan(a, b - a), CellX(line, from, a, originX, font), cell, font, _settings.Foreground);
        }
    }

    private void DrawCellCharSelection(GdiCanvas ink, string line, int from, int to, int originX, Rectangle cell, Font font)
    {
        int a = Math.Clamp(Math.Min(_charAnchor, _charFocus), from, to);
        int b = Math.Clamp(Math.Max(_charAnchor, _charFocus), from, to);
        if (b <= a) return;
        int x0 = CellX(line, from, a, originX, font), x1 = CellX(line, from, b, originX, font);
        var rect = Rectangle.Intersect(cell, new Rectangle(x0, cell.Top, Math.Max(1, x1 - x0), cell.Height));
        if (rect.Width <= 0) return;
        ink.Fill(rect, _settings.SelectionBack);
        DrawInCell(ink, line.AsSpan(a, b - a), x0, cell, font, _settings.SelectionFore);
    }

    /// <summary>Draws a stretch of a cell's text at an exact x, bounded by the cell so it cannot run into
    /// the next column. Text that would start left of the cell is left alone - it is already drawn, and
    /// moving it to fit would put it somewhere it does not belong.</summary>
    private static void DrawInCell(GdiCanvas ink, ReadOnlySpan<char> text, int x, Rectangle cell, Font font, Color colour)
    {
        if (x < cell.Left || x >= cell.Right) return;
        ink.TextIn(text, new Rectangle(x, cell.Top, cell.Right - x, cell.Height), colour, font,
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

    /// <summary>What a cell shows: the value of the part the column says it shows, wherever that column has
    /// been carried to. A span rather than a string - the paint asks for one per cell per row, and a line
    /// split into dozens of parts would otherwise leave a screenful of substrings behind on every frame.
    /// The paint and the checks both come through here, so they cannot disagree.</summary>
    private ReadOnlySpan<char> CellText(string line, ColumnDef def, TemplateMatch match)
    {
        var (from, to) = CellRange(def, match);
        return to > from ? line.AsSpan(from, to - from) : default;
    }

    /// <summary>The stretch of the line a column shows, as indices into the line. A part of pure literal
    /// text captures nothing, so it draws nothing.</summary>
    private (int From, int To) CellRange(ColumnDef def, TemplateMatch match)
    {
        var template = _doc!.Columns.Compiled;
        if (def.Source < 0 || def.Source >= template.PartCount) return (0, 0);
        int value = template.PartAt(def.Source).Value;
        if (value < 0) return (0, 0);
        var (start, length) = match.Value(value);
        return (start, start + length);
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
        _doc.Columns.Compiled.Match(text, _hitMatch);
        var (from, to) = CellRange(def, _hitMatch);
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

    /// <summary>Held apart from the paint's own match so that a hit test part way through a frame cannot
    /// overwrite what the row being drawn is using.</summary>
    private readonly TemplateMatch _hitMatch = new();

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

        var template = spec.Compiled;
        var match = new TemplateMatch();
        long rows = _doc.RowCount;
        int sampled = 0;
        for (int r = 0; r < VisibleRowCount && sampled < 400; r++)
        {
            long row = _firstRow + r;
            if (row < 0 || row >= rows) break;
            string text = _doc.GetLineText(_doc.RowToLine(row));
            sampled++;
            if (!template.Match(text, match)) continue;
            for (int i = 0; i < n; i++)
            {
                if (!spec.Columns[i].Visible) continue;
                var (from, to) = CellRange(spec.Columns[i], match);
                if (to <= from) continue;
                int w = TextRenderer.MeasureText(text.AsSpan(from, to - from), FontRegular,
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
          .Append(spec.Template).Append('|').Append(spec.Layout).Append('|')
          .Append(_doc.RowCount > 0 ? '1' : '0').Append('|');
        foreach (var c in spec.Columns) sb.Append(c.Name).Append(c.Visible ? '+' : '-').Append(c.Source).Append('\u0001');
        return sb.ToString();
    }

    /// <summary>Resolves every column's drawn width. Cheap, and redone on every use, so a resize or a
    /// window change is felt immediately; only the content measurement behind it is cached.</summary>
    private void EnsureColumnLayout()
    {
        var spec = _doc!.Columns;
        // A column added in code says nothing about which field it shows; settle that here, once, rather
        // than leaving every such spec drawing empty cells. Before the count is taken, because settling can
        // DROP a column that points past the end of the template.
        spec.NormalizeSources();
        int n = spec.Columns.Count;
        if (_colWidths.Length != n) _colWidths = new int[n];
        if (n == 0) return;
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

    /// <summary>Drops a part-of-a-line selection that no longer means what it did when it was made.
    ///
    /// <para>Not the MODE it was made in: split into cells the indices are into the line itself and belong
    /// to one cell, whole lines they are into the line with its tabs expanded. Nor the ARRANGEMENT: hiding
    /// a field or carrying one along the row moves every character after it, so a range kept across that
    /// would pick out text nobody chose - and it is what a filter made from the selection would be built
    /// out of.</para>
    ///
    /// <para>Every path that can rearrange the fields has to come through here: <see cref="RefreshView"/>
    /// for the ones that redraw the world, and <see cref="ColumnsEdited"/> for the ones done in the view
    /// itself - a chip clicked or dragged, the header's tick list - which never call it.</para></summary>
    private void DropSelectionIfArrangementChanged()
    {
        if (_charLine >= 0 && (ColumnsOn != (_charColumn >= 0) || FieldsShape() != _charShape))
            ClearCharSelection();
    }

    /// <summary>One place every in-view column edit ends: the drawn widths are stale, the header and every
    /// row have to be redrawn, and whoever owns the file has to know it now differs from what is on disk.
    /// It does NOT re-measure the content - the measurement's own key covers the changes that could affect
    /// it, so a resize drag does not pay for one on every step of the gesture.</summary>
    private void ColumnsEdited()
    {
        _maxContentWidth = 0;
        DropSelectionIfArrangementChanged();
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

        if (InlineOn) return HandleChipMouseDown(e, clicks);

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
        if (InlineOn) { HandleChipMouseMove(e); return; }

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
        if (InlineOn) { EndChipGesture(); return; }
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

    /// <summary>Puts an edit box over the header cell - or, laid out inline, over the CHIP - which is where
    /// the name is read, so renaming needs no dialog and no hunting for the setting.</summary>
    internal void BeginRename(int index)
    {
        if (_doc is null || index < 0 || index >= _doc.Columns.Columns.Count) return;
        // A hidden field has no header to type over, but it does have a chip - and that chip is exactly
        // where a reader would expect to rename it.
        if (!InlineOn && !_doc.Columns.Columns[index].Visible) return;
        EndRename(commit: true);
        var rect = RenameRect(index);
        if (rect.Width <= 0) return;
        _renameIndex = index;
        _renameBox = new TextBox
        {
            Text = _doc.Columns.Columns[index].Name,
            Bounds = rect,
            BorderStyle = BorderStyle.FixedSingle,
            Font = InlineOn ? ChipFont : FontRegular
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

    /// <summary>Where the edit box goes. Over the header cell where there is one; over the chip where the
    /// strip is chips, because that is the thing the reader pressed - and wide enough to type a name into
    /// even when the chip itself is a stub, without running off the end of the strip.</summary>
    private Rectangle RenameRect(int index)
    {
        if (!InlineOn) return ColumnHeaderRect(index);

        var rect = ChipRectFor(index);
        if (rect.IsEmpty) rect = _chipOverflowRect;
        if (rect.IsEmpty) return Rectangle.Empty;

        int right = ClientSize.Width - RightGutterWidth - ChipGap;
        int width = Math.Min(Math.Max(rect.Width, LogicalToDeviceUnits(110)), Math.Max(LogicalToDeviceUnits(40), right - rect.Left));
        return new Rectangle(rect.Left, rect.Top, width, rect.Height);
    }

    private Rectangle ChipRectFor(int index)
    {
        foreach (var (column, rect) in ChipRects()) if (column == index) return rect;
        return Rectangle.Empty;
    }

    // ---- the header's own menu: everything about a column, where the column is ----

    private void ShowColumnMenu(Point at)
    {
        // Inline has chips, not a header: the field under the pointer is the chip under it, and a column's
        // width, alignment and fit mean nothing to a layout that draws no columns.
        var menu = BuildColumnMenu(InlineOn ? ChipAt(at.X, at.Y) : ColumnAt(at.X));
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
        var forThisColumn = new List<(ToolStripMenuItem Item, bool NeedsAnother, bool NeedsVisible)>();
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
            // still up - and hiding or fitting a column nobody can see does nothing. Renaming is the one
            // exception while the layout is inline: a hidden field still has a chip, so there is somewhere
            // to type its new name.
            bool visible = index >= 0 && spec.Columns[index].Visible;
            foreach (var (item, needsAnother, needsVisible) in forThisColumn)
                item.Enabled = index >= 0 && (visible || (!needsVisible && InlineOn))
                                          && (!needsAnother || VisibleColumnIndices().Count > 1);
        }

        for (int i = 0; i < spec.Columns.Count; i++)
        {
            int which = i;
            var item = new ToolStripMenuItem(spec.Columns[i].Name.Length > 0 ? AsLabel(spec.Columns[i].Name) : $"Column {i + 1}")
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
            string name = AsLabel(spec.Columns[index].Name);
            menu.Items.Add(new ToolStripSeparator());
            var rename = Entry($"&Rename \"{name}\"…", () => BeginRename(index));
            var hide = Entry($"&Hide \"{name}\"", () => SetColumnVisible(index, false));
            forThisColumn.Add((rename, false, false));
            forThisColumn.Add((hide, true, true));
            menu.Items.Add(rename);
            menu.Items.Add(hide);

            // Width, alignment and fitting are the Columns layout's business. Inline draws no columns, so
            // offering them there would be offering commands that do nothing.
            if (!InlineOn)
            {
                var fit = Entry($"Fit \"{name}\" to &Content", () => FitColumnToContent(index));
                var align = new ToolStripMenuItem("&Align");
                foreach (var (text, value) in new[] { ("&Left", ColumnAlign.Left), ("&Right", ColumnAlign.Right), ("&Centre", ColumnAlign.Center) })
                {
                    var a = value;
                    var item = new ToolStripMenuItem(text) { Checked = spec.Columns[index].Align == a };
                    item.Click += (_, _) => { menu.Close(); SetColumnAlign(index, a); };
                    align.DropDownItems.Add(item);
                }
                forThisColumn.Add((fit, false, true));
                forThisColumn.Add((align, false, true));
                menu.Items.Add(fit);
                menu.Items.Add(align);
            }
        }
        SyncTicks();

        menu.Items.Add(new ToolStripSeparator());
        if (!InlineOn) menu.Items.Add(Entry("&Fit All Columns to Window", FitColumnsToWindow));
        menu.Items.Add(Entry("&Field Settings…", () => ColumnSettingsRequested?.Invoke()));
        return menu;

        ToolStripMenuItem Entry(string text, Action run, bool enabled = true)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled };
            item.Click += (_, _) => { menu.Close(); run(); };
            return item;
        }
    }

    /// <summary>A field's name as a menu label. A name is the reader's own text, and an &amp; in it would
    /// otherwise disappear into an underline nobody asked for.</summary>
    private static string AsLabel(string name) => name.Replace("&", "&&", StringComparison.Ordinal);

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

    // ---- the chip strip, which no automation pattern can reach: a click and a drag on owner-drawn boxes ----

    internal Rectangle ChipRectForTesting(int index) => ChipRectFor(index);

    /// <summary>A press and a release on a chip, with nothing in between - which is a click, and what puts
    /// a part away or brings it back.</summary>
    internal void ClickChipForTesting(int index)
    {
        var rect = ChipRectForTesting(index);
        if (rect.IsEmpty) return;
        HandleHeaderMouseDown(new MouseEventArgs(MouseButtons.Left, 1, rect.Left + rect.Width / 2, rect.Top + rect.Height / 2, 0), 1);
        EndColumnGesture();
    }

    /// <summary>Carrying a chip sideways: press on it, move well past the drag threshold, let go.</summary>
    internal void DragChipForTesting(int index, int toIndex)
    {
        var from = ChipRectForTesting(index);
        var to = ChipRectForTesting(toIndex);
        if (from.IsEmpty || to.IsEmpty) return;
        int y = from.Top + from.Height / 2;
        HandleHeaderMouseDown(new MouseEventArgs(MouseButtons.Left, 1, from.Left + from.Width / 2, y, 0), 1);
        int target = toIndex > index ? to.Right - 2 : to.Left + 2;
        HandleHeaderMouseMove(new MouseEventArgs(MouseButtons.Left, 0, target, y, 0));
        EndColumnGesture();
    }

    /// <summary>Two clicks on a chip in quick succession, which is how a field is renamed where it stands.</summary>
    internal void DoubleClickChipForTesting(int index)
    {
        var rect = ChipRectForTesting(index);
        if (rect.IsEmpty) return;
        int x = rect.Left + rect.Width / 2, y = rect.Top + rect.Height / 2;
        HandleHeaderMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0), 1);
        EndColumnGesture();
        HandleHeaderMouseDown(new MouseEventArgs(MouseButtons.Left, 2, x, y, 0), 2);
        EndColumnGesture();
    }

    /// <summary>The pointer resting on a chip, which is what lifts it and offers what it does.</summary>
    internal void HoverChipForTesting(int index)
    {
        var rect = index == OverflowChip ? _chipOverflowRect : ChipRectForTesting(index);
        if (rect.IsEmpty) return;
        var at = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        HandleChipMouseMove(new MouseEventArgs(MouseButtons.None, 0, at.X, at.Y, 0));
        TrackHover(at);
    }

    internal int ChipUnderPointerForTesting => _chipUnderPointer;
    internal static int OverflowChipForTesting => OverflowChip;
    internal int ChipsOverflowingForTesting { get { ChipRects(); return _chipsOverflowing; } }

    /// <summary>Skips the hover countdown and puts the tip up now, and reads back what it is saying - so a
    /// check can watch the words themselves change rather than trusting that they would.</summary>
    internal void ShowTipNowForTesting() => ShowTipNow();
    internal string ShownTipForTesting => _tipShowing ? _tipText : "";

    /// <summary>How tall the text on a chip is drawn, which had better be no taller than the chip.</summary>
    internal int ChipLabelHeightForTesting
        => TextRenderer.MeasureText("Xg", ChipFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;

    /// <summary>Where the rename box sits, so a check can say it landed on the thing that was pressed.</summary>
    internal Rectangle RenameBoxBoundsForTesting => _renameBox?.Bounds ?? Rectangle.Empty;

    internal string ChipNamesForTesting
        => _doc is null ? "" : string.Join(",", _doc.Columns.Columns.Select(c => c.Visible ? c.Name : "(" + c.Name + ")"));

    internal int HeaderHeightForTesting => HeaderHeight;
    internal string DisplayTextForTesting(long row) => DisplayText(row);
    internal string TipForTesting(long row) => BuildTip(row);

    /// <summary>What one cell of one row is drawn with - the paint's own lookup, not a copy of it.</summary>
    internal string CellTextForTesting(long row, int column)
    {
        if (_doc is null || column < 0 || column >= _doc.Columns.Columns.Count) return "";
        string text = _doc.GetLineText(_doc.RowToLine(row));
        _doc.Columns.Compiled.Match(text, _hitMatch);
        return CellText(text, _doc.Columns.Columns[column], _hitMatch).ToString();
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
        _charShape = FieldsShape();
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

    /// <summary>Draws one segment of a line - the whole of it when nothing is wrapped - and the row's own
    /// background behind it, in one call. Returns how wide the content is, which is what the horizontal
    /// scrollbar's range is built from.</summary>
    private int DrawSegment(GdiCanvas ink, string text, int from, int to, Rectangle strip,
                            Color back, Color fore, Font font, int fontIndex, int charWidth)
    {
        var part = text.AsSpan(from, to - from);
        int x = strip.Left - (Wrapping ? 0 : _hScroll);
        bool plain = charWidth > 0 && part.IndexOfAnyExceptInRange(' ', '~') < 0;
        var (shownFrom, shownTo) = plain ? OnScreenPart(part.Length, x, charWidth) : (0, part.Length);
        ink.Text(part[shownFrom..shownTo], x + shownFrom * charWidth, strip.Top, strip, fore, back,
                 font, _faces[fontIndex], plain);
        if (Wrapping) return 0;   // nothing scrolls sideways while wrapping, so nothing to measure against
        return (plain ? part.Length * charWidth : DrawnWidth(part, font, charWidth)) + 8;
    }

    /// <summary>
    /// Which characters of a fixed-pitch stretch of text starting at <paramref name="x"/> can land in the
    /// window, with one character of slack at each end for a slanted face's overhang.
    ///
    /// <para>GDI lays out and rasterises every character it is handed, whether or not the result falls
    /// inside the clip - so drawing a whole line costs what the whole line costs, and a log line is
    /// routinely three or four times the width of the window it is being read in. Handing over only the
    /// part that can be seen measured a 400-character log at less than half its cost, and cannot change a
    /// pixel of what appears: what is left out is outside the clip either way.</para>
    /// </summary>
    private (int From, int To) OnScreenPart(int length, int x, int charWidth)
    {
        if (_wholeLines) return (0, length);
        int left = GutterWidth(), right = left + ContentWidth;
        int first = Math.Clamp((left - x) / charWidth - 1, 0, length);
        int last = Math.Clamp((right - x) / charWidth + 2, first, length);
        return (first, last);
    }

    private bool _wholeLines;

    /// <summary>Test seam: hand GDI the whole of every line, as this used to, so a check can prove that
    /// drawing only the part that shows draws the same picture.</summary>
    [System.ComponentModel.DefaultValue(false)]
    internal bool DrawWholeLinesForTesting
    {
        get => _wholeLines;
        set { _wholeLines = value; Invalidate(); }
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
    private void DrawCharSelection(GdiCanvas ink, string text, int from, int to, Rectangle strip,
                                   Font font, int fontIndex)
    {
        int a = Math.Clamp(Math.Min(_charAnchor, _charFocus), 0, text.Length);
        int b = Math.Clamp(Math.Max(_charAnchor, _charFocus), 0, text.Length);
        a = Math.Max(a, from);
        b = Math.Min(b, to);
        if (b <= a) return;
        int x0 = SegmentX(text, from, a, strip.Left, font);
        int x1 = SegmentX(text, from, b, strip.Left, font);
        var box = Rectangle.Intersect(new Rectangle(x0, strip.Top, Math.Max(1, x1 - x0), _rowHeight), strip);
        ink.Fill(box, _settings.SelectionBack);
        var part = text.AsSpan(a, b - a);
        ink.TextOver(part, x0, strip.Top, strip, _settings.SelectionFore, font, _faces[fontIndex],
                     Plain(part, fontIndex));
    }

    /// <summary>Where a character sits on screen, measured from the start of the segment it is drawn in.</summary>
    private int SegmentX(string text, int segmentStart, int index, int left, Font font)
        => left - (Wrapping ? 0 : _hScroll) + PrefixWidth(text.AsSpan(segmentStart), index - segmentStart, font);

    /// <summary>The margins beside a row: the marker bars and the line number, on the neutral margin colour
    /// rather than the row's own - a marker has to stay findable whatever colour a filter gave the line.
    /// <para>The number is drawn over a background it is told about, and placed by arithmetic in a
    /// fixed-pitch face rather than by asking the text layout to right-align it. Between them those took a
    /// fifth of the whole paint off it.</para></summary>
    private void DrawGutters(GdiCanvas ink, long line, int y, int height, bool selected)
    {
        int markers = MarkerGutterWidth;
        if (markers > 0)
        {
            ink.Fill(new Rectangle(0, y, markers, height), _settings.GutterBack);
            if (_doc?.Markers.MaskOf(line) is var mask && mask is not (null or 0))
                for (int m = 0; m < 8; m++)
                {
                    if ((mask.Value & (1 << m)) == 0) continue;
                    ink.Fill(new Rectangle(3 + m * 5, y + 2, 4, _rowHeight - 4), AppSettings.MarkerColors[m]);
                }
        }

        int lnw = LineNumberGutterWidth;
        if (lnw == 0) return;
        var colour = selected ? _settings.SelectionBack : _settings.LineNumberColor;
        var box = new Rectangle(markers, y, lnw, _rowHeight);
        var digits = Digits(line + 1);
        int digitWidth = _longWay ? 0 : CharWidthOf(0);
        if (digitWidth > 0)
            ink.Text(digits, box.Right - 6 - digits.Length * digitWidth, y, box, colour, _settings.GutterBack,
                     FontRegular, _faces[0], plain: true);
        else
        {
            // Text laid out over a background colour fills the character cells, not the box around them,
            // so the margin has to be laid down first.
            ink.Fill(box, _settings.GutterBack);
            ink.TextIn(digits, new Rectangle(markers, y, lnw - 6, _rowHeight), colour, FontRegular,
                       TextFormatFlags.NoPadding | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        }

        // A wrapped row is several lines tall and the number belongs beside the first of them; the rest of
        // the margin still has to be the margin's colour rather than the row's.
        if (height > _rowHeight)
            ink.Fill(new Rectangle(markers, y + _rowHeight, lnw, height - _rowHeight), _settings.GutterBack);
    }

    /// <summary>A line number as characters, without the string a row's worth of them would allocate on
    /// every frame.</summary>
    private ReadOnlySpan<char> Digits(long value)
    {
        value.TryFormat(_digits, out int written, provider: System.Globalization.CultureInfo.CurrentCulture);
        return _digits.AsSpan(0, written);
    }

    private readonly char[] _digits = new char[24];

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
            _charShape = FieldsShape();
        }
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_doc is not null && _doc.Columns.Active) HandleHeaderMouseMove(e);
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
                _charShape = FieldsShape();
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
            _catchUp.Dispose();
            foreach (var f in _fonts) f?.Dispose();
            ReleaseFaces();
            _canvas.Discard();
            foreach (var b in _brushes.Values) b.Dispose();
            _brushes.Clear();
            _fontFamily?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Gives back the eight faces GDI was asked for. They are handles, not objects: nothing else
    /// would ever let go of them.</summary>
    private void ReleaseFaces()
    {
        for (int i = 0; i < _faces.Length; i++)
        {
            if (_faces[i] != IntPtr.Zero) DeleteObject(_faces[i]);
            _faces[i] = IntPtr.Zero;
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    protected override void OnMouseLeave(EventArgs e)
    {
        HideTip();
        if (_chipUnderPointer != -1) { _chipUnderPointer = -1; Invalidate(); }
        if (_colGesture == ColumnGesture.None) SetCursorTo(Cursors.Default);
        base.OnMouseLeave(e);
    }

    /// <summary>Restarts the hover countdown whenever the pointer moves to a different row, so the tip
    /// describes where the pointer settled rather than where it passed through.</summary>
    private void TrackHover(Point at)
    {
        if (_doc is null || _dragging) { HideTip(); return; }

        // The chips are an affordance, not a filter tip: they say what they do whether or not the reader
        // has asked for tips on lines.
        if (InlineOn && InColumnHeader(at.Y))
        {
            int chip = ChipAt(at.X, at.Y);
            if (chip != _tipChip) { HideTip(); _tipChip = chip; _tipPoint = at; if (chip != -1) { _tipTimer.Stop(); _tipTimer.Start(); } }
            return;
        }
        if (_tipChip != -1) HideTip();

        // A line the template does not match is shown whole while its neighbours are not, and the reason
        // belongs on it whether or not the reader asked for tips about FILTERS - it is a different question.
        long row = RowAtY(at.Y);
        if (at.Y < TextTop || row < 0 || row >= _doc.RowCount) { HideTip(); return; }
        if (!_settings.ShowFilterTooltips && !LineDoesNotMatch(row)) { HideTip(); return; }
        if (row == _tipRow) return;

        HideTip();
        _tipRow = row;
        _tipPoint = at;
        _tipTimer.Stop();
        _tipTimer.Start();
    }

    private bool LineDoesNotMatch(long row)
        => _doc is not null && _doc.Columns.Active &&
           !_doc.Columns.Compiled.Match(_doc.GetLineText(_doc.RowToLine(row)), _hitMatch);

    private void HideTip()
    {
        _tipTimer.Stop();
        if (_tipRow >= 0 || _tipChip != -1) _tips.Hide(this);
        _tipShowing = false;
        _tipText = "";
        _tipRow = -1;
        _tipChip = -1;
    }

    /// <summary>Whether a tip is on screen at this moment, as against merely counting down to one. What a
    /// chip's tip says depends on the state of that chip, so a click that changes the state has to put the
    /// words right - but only if there are words up there to be wrong.</summary>
    private bool _tipShowing;
    private string _tipText = "";

    /// <summary>Says the chip's tip again, now that clicking it has changed what there is to say. Left
    /// alone, the tip that was open when the chip was clicked stayed open and went on offering to do the
    /// thing that had just been done.</summary>
    private void RefreshChipTip(int chip)
    {
        if (!_tipShowing || _tipChip != chip) return;
        ShowTipNow();
    }

    private void ShowTipNow()
    {
        _tipTimer.Stop();
        if (_doc is null) return;

        if (_tipChip >= 0)
        {
            if (_tipChip >= _doc.Columns.Columns.Count) return;
            Say(ChipTipText(_tipChip));
            return;
        }

        if (_tipChip == OverflowChip)
        {
            Say($"{_chipsOverflowing} more field{(_chipsOverflowing == 1 ? "" : "s")} than there is room for.\nClick for the whole list.");
            return;
        }

        if (_tipRow < 0 || _tipRow >= _doc.RowCount) return;
        string text = BuildTip(_tipRow);
        if (text.Length == 0) return;
        Say(text);

        void Say(string words)
        {
            _tips.Show(words, this, _tipPoint.X + 16, _tipPoint.Y + 20, TipDurationMs);
            _tipText = words;
            _tipShowing = true;
        }
    }

    /// <summary>What a chip's tip says, which is entirely about the state that chip is in - so it has to be
    /// worked out afresh every time it is put up, and again whenever a click changes that state.</summary>
    private string ChipTipText(int chip)
    {
        var def = _doc!.Columns.Columns[chip];
        bool lastOne = def.Visible && _doc.Columns.Columns.Count(c => c.Visible) <= 1;
        return lastOne
            ? $"\u201c{def.Name}\u201d is the only field still shown, so it cannot be left out too.\nDrag to move it along the row, or double-click to rename it."
            : def.Visible
                ? $"\u201c{def.Name}\u201d is being shown.\nClick to leave it out, drag to move it along the row, double-click to rename it."
                : $"\u201c{def.Name}\u201d is being left out.\nClick to bring it back, drag to move it along the row, double-click to rename it.";
    }

    /// <summary>What a hover says about a line: which filters matched it, and - when the template does not
    /// fit - why this one is being shown whole while its neighbours are not.</summary>
    private string BuildTip(long row)
    {
        if (_doc is null) return "";
        long line = _doc.RowToLine(row);
        string text = _settings.ShowFilterTooltips ? FilterTipText.Build(_doc.FiltersMatching(line)) : "";

        if (_doc.Columns.Active && !_doc.Columns.Compiled.Match(_doc.GetLineText(line), _hitMatch))
        {
            const string note = "This line does not match the template, so it is shown whole.";
            text = text.Length == 0 ? note : text + "\n\n" + note;
        }
        return text;
    }

    /// <summary>Builds the tip a hover would show, without the wait or the window.</summary>
    internal string TipTextForTesting(long row) => BuildTip(row);

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

    /// <summary>The same, for a change to the FIELDS - where how many rows the strip above the text will
    /// take is the grid's own business and not something the caller should have to predict. It depends on
    /// the layout being switched to and on the two fonts in play, and a caller that guessed one row would
    /// slide the whole log the moment that stopped being true.</summary>
    internal void KeepTextStillAcrossFieldChange(Action change)
    {
        int before = HeaderRows;
        KeepTextStillAcross(0, () => { change(); });
        int after = HeaderRows;
        if (after == before) return;
        SetFirstRow(_firstRow + (after - before));
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
        var eval = _doc.ColouringSnapshot().Evaluate(text, line);
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
            return n > 0 ? n : (StandInLine >= 0 ? 1 : 0);
        }
    }

    /// <summary>The selected lines the view is showing, in order. Walked through the rows rather than
    /// through the lines, so a selection spanning a hidden million costs only what it yields.</summary>
    private IEnumerable<long> SelectedLines(long cap)
    {
        if (_doc is null) yield break;
        // Whatever is highlighted is what a copy or a marker acts on, and while the chosen lines are all
        // hidden that is the stand-in.
        if (StandInLine is var stand && stand >= 0)
        {
            if (cap > 0) yield return stand;
            yield break;
        }
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
        // Counted before the copy, not after: a filter pass settling in between would otherwise leave a
        // total that the copy never worked against, and "42 of 3 lines" is worse than saying nothing.
        long selected = SelectedCount;
        string text = BuildCopyText(withLineNumbers, out long copied);
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); } catch { return; /* clipboard busy: nothing was copied */ }
        if (copied < selected) CopyTruncated?.Invoke(copied, selected);
    }

    /// <summary>The selected lines as one block of text, up to <see cref="CopyCharCap"/>. What is copied is
    /// what is SHOWN - so a tab reads as the spaces it was drawn as, and a line shortened by the Inline
    /// layout is copied as short as it looks. Picking part of a line out already worked that way, and the
    /// two disagreeing would be worse than either answer.</summary>
    internal string BuildCopyText(bool withLineNumbers, out long copied)
    {
        copied = 0;
        if (_doc is null) return "";
        var sb = new StringBuilder();
        foreach (long line in SelectedLines(CopyLineCap))
        {
            if (sb.Length >= CopyCharCap) break;
            if (withLineNumbers) sb.Append(line + 1).Append('\t');
            sb.AppendLine(DisplayTextOf(line));
            copied++;
        }
        return sb.ToString();
    }

    /// <summary>A row's text as the reader is seeing it, which is what the clipboard, a filter seeded from
    /// it and the search box all work from.</summary>
    public string DisplayedLineText(long line) => DisplayTextOf(line);

    /// <summary>Whether the live find term can actually be SEEN on a line. A search runs on the whole raw
    /// line, so while fields are being hidden it can land the reader on a line where nothing lights up -
    /// which reads as the search being broken. Answering this lets the caller say so.</summary>
    public bool FindTermIsVisibleOn(long line)
    {
        if (_doc is null || _highlight is null || line < 0) return true;
        var spec = _doc.Columns;
        if (!spec.Active) return true;

        string raw = _doc.GetLineText(line);
        if (!_highlight.Matches(raw)) return true;      // not on this line at all; nothing is being hidden

        if (spec.Layout == FieldLayout.Inline) return _highlight.Matches(DisplayTextOf(line));

        // Laid out in columns there is no one string to look at. Walk the hits on the RAW line and ask
        // whether any of them lands on something still shown - asking the pattern about each value on its
        // own instead would take away the context it may depend on (a lookahead at the bracket after a
        // field would stop matching), and would miss a hit that runs across two cells.
        var template = spec.Compiled;
        if (!template.Match(raw, _hitMatch)) return true;   // a line it does not match is drawn whole

        int from = 0;
        while (_highlight.NextMatch(raw, from, out int at, out int length))
        {
            length = Math.Max(1, length);
            foreach (var column in spec.Columns)
            {
                if (!column.Visible || column.Source < 0 || column.Source >= template.PartCount) continue;
                int value = template.PartAt(column.Source).Value;
                if (value < 0) continue;
                var (start, size) = _hitMatch.Value(value);
                if (size > 0 && at < start + size && at + length > start) return true;
            }
            from = at + length;
        }
        return false;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); RefreshView(); }

    /// <summary>The chips are labelled in the window's own font, so they have to be re-cut when it changes -
    /// which it does when the reader moves the window to a screen at another scaling.</summary>
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        BuildChipFont();
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RebuildFonts();
    }

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
                long line = _g.LineAt(Row);
                if (_g._sel.Contains(line) || line == _g.StandInLine) s |= AccessibleStates.Selected;
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

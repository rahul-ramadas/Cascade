using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Document;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// The log seen from far enough away that a row is a pixel: one pixel row per display row, in exactly the
/// colour that row is painted in the text area. Nothing is invented here - a filter you have given no colour
/// shows as nothing, which is the honest answer.
///
/// A pixel per row means the map holds only as many rows as it is tall, so it shows a <b>window</b> of the
/// file rather than the whole of it, centred on the view. That is the same trade Wireshark makes with its
/// scrollbar overlay, and it is why the scrollbar beside it keeps the whole-file scale.
///
/// With unmatched lines on screen (dim mode) that window would be nearly empty - a filter set matching half a
/// percent of a file leaves four coloured pixels in nine hundred - so a <b>run of unmatched rows is
/// compressed</b> to a fraction of its height while every matched row keeps its own pixel. The view then
/// reaches thirty times further without losing a single match, or the size of a burst of them. The price is
/// that the vertical scale is no longer linear: distance on the map is not distance in the file. The
/// scrollbar next door is the linear one.
/// </summary>
internal sealed class MiniMapControl : Control
{
    /// <summary>Logical width. Wide enough to hit with a mouse and to read a colour off.</summary>
    public const int LogicalWidth = 18;

    /// <summary>Unmatched rows skipped per pixel of gap. Only matched rows are worth a pixel each; this is
    /// how much of the space between them the map is prepared to spend.</summary>
    private const int GapRows = 32;

    private const int EdgeLane = 3;           // logical; marks down the left, find hits down the right
    private const int MinViewportHeight = 8;  // a compressed stretch would otherwise leave it a hairline
    private const int HoverDelayMs = 400;
    private const int TipDurationMs = 8000;

    private readonly LineGridControl _grid;
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = HoverDelayMs };

    private long[] _rowAt = Array.Empty<long>();   // the row behind each pixel row
    private int[] _colour = Array.Empty<int>();    // 0 where nothing is painted
    private Bitmap? _picture;
    private int _rowPixels = 1;
    private int _slots;                            // pixel rows in use
    private long _top;                             // first row the window shows
    private long _span = 1;                        // rows the window covered when it was last built

    private int _builtGeneration = -1;
    private long _builtRows = -1;
    private long _builtTop = -1;
    private int _builtHeight = -1;
    private int _builtWidth = -1;
    private bool _builtFilteredMode;
    private int _builtMarkers = -1;
    private long _builtFindHits = -1;
    private long _drawnSelection = -1;
    private (int Top, int Height) _drawnViewport = (-1, -1);

    private bool _dragging;
    private bool _hovering;
    private long _trackedView = -1;   // the view position the window was last placed for
    private int _grabOffset;         // where inside the rectangle the drag took hold of it
    private int _tipSlot = -1;
    private Point _tipPoint;
    private int _paints;

    public MiniMapControl(LineGridControl grid)
    {
        _grid = grid;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Right;
        Width = LogicalToDeviceUnits(LogicalWidth);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Minimap";
        _tipTimer.Tick += (_, _) => ShowTipNow();
    }

    protected override void Dispose(bool disposing)
    {
        // None of these is a child control, so nothing else would clean them up.
        if (disposing) { _tipTimer.Dispose(); _tips.Dispose(); _picture?.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Throws away the summary so the next paint rebuilds it.</summary>
    public void InvalidateSummary() { _builtGeneration = -1; _trackedView = -1; Invalidate(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Width = LogicalToDeviceUnits(LogicalWidth);
    }

    // ---- test seams: the summary, rather than a picture of it ----

    internal void RebuildForTesting() { _builtGeneration = -1; EnsureSummary(); }
    internal int SlotCountForTesting => _slots;
    internal int RowPixelsForTesting => _rowPixels;
    internal long TopRowForTesting => _top;
    internal long SpanForTesting => _span;
    internal long RowAtForTesting(int slot) => slot >= 0 && slot < _slots ? _rowAt[slot] : -1;
    internal int ColourAtForTesting(int slot) => slot >= 0 && slot < _slots ? _colour[slot] : 0;
    internal int SlotOfForTesting(long row) => SlotOf(row);
    internal (long From, long To) RowsAtForTesting(int slot) => slot >= 0 && slot < _slots ? RowsAt(slot) : (-1, -1);
    internal long[] RowsForTesting() => _rowAt.Take(_slots).ToArray();
    internal string TipTextForTesting(int slot) => SlotTipText(slot);
    internal void ClickForTesting(int y) { Grab(y); DropForTesting(); LeaveForTesting(); }
    internal void GrabForTesting(int y) { _dragging = true; _hovering = true; Grab(y); }
    internal void DragToForTesting(int y) => ScrollToTop(y - _grabOffset);
    internal void DropForTesting() { _dragging = false; }
    internal void LeaveForTesting() { _hovering = false; }
    internal (int Top, int Height) ViewportForTesting => Geometry();
    internal Rectangle ContentForTesting => new(Divider, 0, Math.Max(1, ClientSize.Width - Divider), ClientSize.Height);

    /// <summary>How many times the map has actually painted. A picture of it cannot answer that: capturing a
    /// control draws it whether or not it was invalidated, so a screenshot always looks up to date even when
    /// the real window has been sitting stale for minutes.</summary>
    internal int PaintsForTesting => _paints;

    // ---- geometry ----

    private int MinRowPixels => Math.Max(1, LogicalToDeviceUnits(1));
    private int Divider => Math.Max(1, LogicalToDeviceUnits(1));
    private int EdgeWidth => Math.Max(2, LogicalToDeviceUnits(EdgeLane));

    /// <summary>Slots the map has room for at the size it is now.</summary>
    private int SlotCapacity => Math.Max(1, Math.Max(1, ClientSize.Height) / Math.Max(1, _rowPixels));

    /// <summary>Puts the view back in the middle of the window, but only when the view has moved for a
    /// reason of its own - a key, the wheel, the scrollbar. Returns whether it moved the window.
    ///
    /// Re-centring on every look would undo the map's own dragging: the drop position would spring back to
    /// the middle the moment the pointer left. So a scroll the map itself caused is recorded as accounted
    /// for and left alone, and the window only re-centres when something else moves the view.</summary>
    private bool TrackView(long rows, int slots)
    {
        long viewTop = _grid.FirstVisibleRow;
        if (_dragging || _hovering) { _trackedView = viewTop; return false; }
        if (viewTop == _trackedView) return false;
        _trackedView = viewTop;
        Recentre(rows, slots);
        return true;
    }

    /// <summary>Starts the window half a map above the view, measured in the pixels the fill will actually
    /// spend rather than in rows. Guessing from the last build's span cannot work: a build that crosses
    /// into a compressed stretch reaches many times further than the one before it, so the guess and the
    /// correction chase each other and the view can settle hard against an edge.</summary>
    private void Recentre(long rows, int slots)
    {
        if (_grid.Document is not { } doc || rows <= 0) return;
        long viewTop = Math.Clamp(_grid.FirstVisibleRow, 0, rows - 1);
        _top = BackFrom(doc, viewTop, slots / 2);
    }

    /// <summary>Where the map has to begin for <paramref name="from"/> to land <paramref name="pixels"/>
    /// down it: the fill's own gap rule walked backwards, without resolving any colours.</summary>
    private static long BackFrom(CascadeDocument doc, long from, int pixels)
    {
        long row = from;
        int spent = 0;
        while (spent < pixels && row > 0)
        {
            long stop = PrevStop(doc, row);
            if (stop < row)
            {
                long gap = row - stop;
                int cost = (int)Math.Max(1, Math.Min(int.MaxValue, gap / GapRows));
                if (spent + cost >= pixels) return Math.Max(0, row - (pixels - spent) * gap / cost);
                spent += cost;
                row = Math.Max(0, stop);
                continue;
            }
            spent++;
            row--;
        }
        return Math.Max(0, row);
    }

    private void EnsureSummary()
    {
        var doc = _grid.Document;
        if (doc is null) { _slots = 0; return; }

        long rows = doc.RowCount;
        int height = Math.Max(1, ClientSize.Height);
        int width = Math.Max(1, ClientSize.Width);
        long findHits = doc.FindHitCount;

        _rowPixels = MinRowPixels;
        int slots = Math.Max(1, height / _rowPixels);
        // Few enough rows to fit outright: give each one a taller band rather than leaving the map half empty.
        if (rows > 0 && rows <= slots)
        {
            _rowPixels = Math.Max(_rowPixels, height / (int)Math.Max(1, rows));
            slots = Math.Max(1, height / _rowPixels);
        }
        TrackView(rows, slots);

        // The selection is drawn over the picture rather than into it, so moving the caret needs a repaint
        // but not a rebuild - which matters, because holding an arrow key asks for one per keypress.
        if (_builtGeneration == doc.FilterGeneration && _builtRows == rows && _builtTop == _top &&
            _builtHeight == height && _builtWidth == width && _builtFilteredMode == doc.FilteredMode &&
            _builtMarkers == doc.Markers.Version && _builtFindHits == findHits)
            return;

        _builtGeneration = doc.FilterGeneration;
        _builtRows = rows;
        _builtHeight = height;
        _builtWidth = width;
        _builtFilteredMode = doc.FilteredMode;
        _builtMarkers = doc.Markers.Version;
        _builtFindHits = findHits;

        if (_rowAt.Length < slots) { _rowAt = new long[slots]; _colour = new int[slots]; }

        _top = Math.Clamp(_top, 0, Math.Max(0, rows - 1));
        _slots = rows <= 0 ? 0 : Fill(doc, rows, slots);
        // Ran out of file before the map was full: anchor to the end instead, so the last row of the file is
        // the last pixel. Otherwise the map empties out as you reach the bottom.
        if (_slots < slots && _top > 0 && rows > 0)
        {
            _slots = FillBackward(doc, rows, slots);
            _top = _slots > 0 ? _rowAt[0] : 0;
        }
        _span = Math.Max(1, (_slots > 0 ? _rowAt[_slots - 1] + 1 : _top + 1) - _top);
        _builtTop = _top;
        RedrawPicture(width, height);
    }

    /// <summary>Walks rows forward into pixel slots: one slot per matched row, and one slot per
    /// <see cref="GapRows"/> unmatched rows in between. Returns how many slots were filled.</summary>
    private int Fill(CascadeDocument doc, long rows, int slots)
    {
        var settings = _grid.Settings;
        var defaults = Defaults(settings);
        int at = 0;
        long row = _top;
        while (at < slots && row < rows)
        {
            long stop = NextStop(doc, rows, row);
            if (stop > row)
            {
                // Nothing matches between here and there, so the whole stretch is worth a few pixels - at a
                // fixed rate, so that one enormous gap does not quietly rescale the whole map.
                long gap = stop - row;
                int wanted = (int)Math.Max(1, Math.Min(int.MaxValue, gap / GapRows));
                int pixels = Math.Min(wanted, slots - at);
                for (int i = 0; i < pixels; i++)
                {
                    _rowAt[at] = row + gap * i / wanted;
                    _colour[at] = 0;
                    at++;
                }
                if (pixels < wanted) break;   // the map filled up part way through the gap
                row = stop;
                continue;
            }
            _rowAt[at] = row;
            _colour[at] = ColourOf(doc, doc.RowToLine(row), defaults, settings);
            at++;
            row++;
        }
        return at;
    }

    /// <summary>The same walk from the end of the file backwards, for when there is not enough file left
    /// below the window to fill it. Returns how many slots were filled, packed to the start.</summary>
    private int FillBackward(CascadeDocument doc, long rows, int slots)
    {
        var settings = _grid.Settings;
        var defaults = Defaults(settings);
        int at = slots - 1;
        long row = rows - 1;
        while (at >= 0 && row >= 0)
        {
            long stop = PrevStop(doc, row);
            if (stop < row)
            {
                long gap = row - stop;
                int wanted = (int)Math.Max(1, Math.Min(int.MaxValue, gap / GapRows));
                int pixels = Math.Min(wanted, at + 1);
                for (int i = 0; i < pixels; i++)
                {
                    _rowAt[at] = row - gap * i / wanted;
                    _colour[at] = 0;
                    at--;
                }
                if (pixels < wanted) break;
                row = stop;
                continue;
            }
            _rowAt[at] = row;
            _colour[at] = ColourOf(doc, doc.RowToLine(row), defaults, settings);
            at--;
            row--;
        }
        if (at < 0) return slots;
        int filled = slots - at - 1;
        Array.Copy(_rowAt, at + 1, _rowAt, 0, filled);
        Array.Copy(_colour, at + 1, _colour, 0, filled);
        return filled;
    }

    /// <summary>Where the walk stops next going down: the next matched row, or the end of the file.
    ///
    /// The caret deliberately does NOT stop it. Stopping here would split the stretch it sits in, and the
    /// two halves round to a different number of pixels than the whole did - so every arrow key re-laid out
    /// everything below the caret and the map shivered. The caret is drawn from the rows a pixel stands for
    /// instead, so it still shows up wherever it is without having a pixel to itself.</summary>
    private static long NextStop(CascadeDocument doc, long rows, long row)
    {
        long nextLine = doc.NextMatchedLine(doc.RowToLine(row));
        return nextLine < 0 ? rows : Math.Min(rows, doc.RowAtOrAfterLine(nextLine));
    }

    private static long PrevStop(CascadeDocument doc, long row)
    {
        long prevLine = doc.PrevMatchedLine(doc.RowToLine(row));
        return prevLine < 0 ? -1 : doc.RowForLine(prevLine);
    }

    private static ResolvedStyle Defaults(AppSettings settings) => new(
        new RgbColor(settings.Foreground.R, settings.Foreground.G, settings.Foreground.B),
        new RgbColor(settings.Background.R, settings.Background.G, settings.Background.B), false, false);

    /// <summary>The colour the text area paints this line's background in, or its text colour when the
    /// filter sets only that, or nothing at all. Resolved through the same evaluation the grid uses, so the
    /// map cannot come to a different answer than the row it stands for.</summary>
    private static int ColourOf(CascadeDocument doc, long line, ResolvedStyle defaults, AppSettings settings)
    {
        var eval = doc.EvaluateText(doc.GetLineText(line), line);
        if (eval.ColorFilter is not { } filter) return 0;
        var style = StyleResolver.Resolve(filter, defaults);
        var bg = Color.FromArgb(style.Background.R, style.Background.G, style.Background.B);
        if (bg.ToArgb() != settings.Background.ToArgb()) return bg.ToArgb();
        var fg = Color.FromArgb(style.Foreground.R, style.Foreground.G, style.Foreground.B);
        return fg.ToArgb() != settings.Foreground.ToArgb() ? fg.ToArgb() : 0;
    }

    /// <summary>The slot standing for a row: the last one whose own row is at or before it, so it is the
    /// pixel that row is actually drawn on even where a stretch was compressed and one pixel covers thirty.
    /// The rows increase across the slots but not evenly, so this is a search.</summary>
    private int SlotOf(long row)
    {
        if (_slots <= 0) return -1;
        if (row <= _rowAt[0]) return 0;
        if (row >= _rowAt[_slots - 1]) return _slots - 1;
        int lo = 0, hi = _slots - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_rowAt[mid] <= row) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The rows a slot stands for, which is more than one wherever a stretch was compressed.</summary>
    private (long From, long To) RowsAt(int slot)
        => (_rowAt[slot], slot + 1 < _slots ? Math.Max(_rowAt[slot] + 1, _rowAt[slot + 1]) : _rowAt[slot] + 1);

    // ---- painting ----

    private void RedrawPicture(int width, int height)
    {
        _picture?.Dispose();
        _picture = null;
        if (_slots <= 0) return;

        var settings = _grid.Settings;
        int backArgb = settings.GutterBack.ToArgb();
        int dividerArgb = Blend(settings.Foreground, settings.GutterBack, 0.30).ToArgb();
        int left = Divider;

        var picture = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = picture.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[width];
            for (int y = 0; y < height; y++)
            {
                int slot = y / _rowPixels;
                int argb = slot < _slots && _colour[slot] != 0 ? _colour[slot] : backArgb;
                Array.Fill(row, argb);
                // A rule down the left, or a coloured row runs straight into the text beside it and there is
                // no telling where one ends and the other starts.
                for (int x = 0; x < left && x < width; x++) row[x] = dividerArgb;
                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally { picture.UnlockBits(data); }
        _picture = picture;
    }

    internal static Color Blend(Color c, Color back, double t) => Color.FromArgb(
        (int)Math.Round(back.R + (c.R - back.R) * t),
        (int)Math.Round(back.G + (c.G - back.G) * t),
        (int)Math.Round(back.B + (c.B - back.B) * t));

    protected override void OnPaint(PaintEventArgs e)
    {
        _paints++;
        var g = e.Graphics;
        g.Clear(_grid.Settings.GutterBack);
        EnsureSummary();
        if (_grid.Document is not { } doc || _slots <= 0) return;

        if (_picture is { } picture) g.DrawImageUnscaled(picture, 0, 0);
        DrawMarks(g, doc);
        DrawViewport(g);
    }

    private void DrawMarks(Graphics g, CascadeDocument doc)
    {
        int edge = EdgeWidth, left = Divider;
        long first = _rowAt[0], last = _rowAt[_slots - 1];
        _drawnSelection = _grid.SelectionVersion;

        // Selected rows and find hits share the map with the colours, so they take an edge each rather than
        // covering the row they belong to. The selection goes down first: a mark is deliberate and stays,
        // a selection is wherever you last clicked, and the two want the same few pixels.
        if (_grid.HasSelection)
        {
            using var brush = new SolidBrush(_grid.Settings.SelectionBack);
            for (int s = 0; s < _slots; s++)
            {
                var (from, to) = RowsAt(s);
                if (_grid.SelectionIntersects(from, to))
                    g.FillRectangle(brush, left, s * _rowPixels, edge, Math.Max(2, _rowPixels));
            }
        }

        foreach (var (line, mask) in doc.Markers.Snapshot())
        {
            long row = doc.FilteredMode ? doc.RowForLine(line) : line;
            if (row < first || row > last) continue;
            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            using var brush = new SolidBrush(AppSettings.MarkerColors[Math.Clamp(index, 0, AppSettings.MarkerColors.Length - 1)]);
            g.FillRectangle(brush, left, SlotOf(row) * _rowPixels, edge, Math.Max(2, _rowPixels));
        }

        if (doc.FindHitCount > 0)
        {
            using var brush = new SolidBrush(_grid.Settings.FindCurrent);
            int x = Math.Max(left, ClientSize.Width - edge);
            for (int s = 0; s < _slots; s++)
            {
                var (from, to) = RowsAt(s);
                long lineFrom = doc.RowToLine(from);
                long lineTo = doc.RowToLine(Math.Max(from, to - 1)) + 1;
                if (doc.FindHitsInRange(lineFrom, Math.Max(lineFrom + 1, lineTo)) > 0)
                    g.FillRectangle(brush, x, s * _rowPixels, edge, Math.Max(2, _rowPixels));
            }
        }
    }

    private (int Top, int Height) Geometry()
    {
        if (_slots <= 0) return (0, MinViewportHeight);
        long viewTop = _grid.FirstVisibleRow;
        long viewRows = Math.Max(1, _grid.VisibleRows);
        int top = SlotOf(viewTop) * _rowPixels;
        int bottom = (SlotOf(viewTop + viewRows - 1) + 1) * _rowPixels;
        int height = Math.Max(MinViewportHeight, bottom - top);
        int limit = Math.Max(1, ClientSize.Height);
        if (top + height > limit) top = Math.Max(0, limit - height);
        return (top, height);
    }

    private void DrawViewport(Graphics g)
    {
        var (top, height) = Geometry();
        _drawnViewport = (top, height);

        var settings = _grid.Settings;
        int left = Divider;
        int width = Math.Max(1, ClientSize.Width - left);
        using (var fill = new SolidBrush(Color.FromArgb(40, settings.SelectionBack)))
            g.FillRectangle(fill, left, top, width, height);
        using var pen = new Pen(Color.FromArgb(210, settings.SelectionBack));
        g.DrawRectangle(pen, left, top, width - 1, height - 1);
    }

    /// <summary>Repaints when anything the map draws has actually changed. The map is a child control, so the
    /// grid invalidating itself does not touch it - without this the picture would sit exactly as it was last
    /// painted while the text scrolled under it.</summary>
    internal void SyncToGrid()
    {
        if (!Visible || _grid.Document is not { } doc) return;
        if (_builtRows != doc.RowCount || _builtFilteredMode != doc.FilteredMode ||
            _builtGeneration != doc.FilterGeneration || _builtMarkers != doc.Markers.Version ||
            _builtFindHits != doc.FindHitCount || _drawnSelection != _grid.SelectionVersion)
        {
            Invalidate();
            return;
        }
        if (_slots <= 0) return;
        long rows = doc.RowCount;
        long before = _top;
        if (rows > 0) TrackView(rows, SlotCapacity);
        if (_top != before || Geometry() != _drawnViewport) Invalidate();
    }

    // ---- interaction ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        HideTip();
        Grab(e.Y);
    }

    /// <summary>Takes hold at a pixel row. Inside the window that is a grab, and it stays exactly where it
    /// is under the pointer; outside it, the view jumps there first with the window centred on the
    /// pointer.</summary>
    private void Grab(int y)
    {
        var (top, height) = Geometry();
        if (y >= top && y < top + height) { _grabOffset = y - top; return; }
        _grabOffset = height / 2;
        ScrollToTop(y - _grabOffset);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hovering = true;
        if (_dragging) { ScrollToTop(e.Y - _grabOffset); return; }
        TrackHover(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovering = true;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovering = false;
        HideTip();
        base.OnMouseLeave(e);
        Invalidate();   // frozen while the pointer was here; it has a window to catch up on
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _grid.ScrollByWheel(e.Delta);
    }

    /// <summary>Puts the top of the view at the pixel row <paramref name="y"/>, without letting it leave the
    /// stretch of file the map is showing - dragging past either end would otherwise carry the view on while
    /// the rectangle sat stuck against the edge, doing nothing you could see.</summary>
    private void ScrollToTop(int y)
    {
        if (_slots <= 0) return;
        int slot = Math.Clamp(y / _rowPixels, 0, _slots - 1);
        long lowest = _rowAt[0];
        long highest = Math.Max(lowest, _rowAt[_slots - 1] - _grid.VisibleRows + 1);
        _grid.ScrollToRow(Math.Clamp(_rowAt[slot], lowest, highest));
        // A held drag never lets the queue empty, and WM_PAINT only arrives when it does - so without these
        // nothing moves until the mouse stops.
        _grid.Update();
        Update();
    }

    // ---- what is under the pointer ----

    private void TrackHover(Point at)
    {
        int slot = at.Y / Math.Max(1, _rowPixels);
        if (slot < 0 || slot >= _slots) { HideTip(); return; }
        if (slot == _tipSlot) return;
        HideTip();
        _tipSlot = slot;
        _tipPoint = at;
        _tipTimer.Stop();
        _tipTimer.Start();
    }

    private void HideTip()
    {
        _tipTimer.Stop();
        if (_tipSlot >= 0) _tips.Hide(this);
        _tipSlot = -1;
    }

    private void ShowTipNow()
    {
        _tipTimer.Stop();
        string text = SlotTipText(_tipSlot);
        if (text.Length == 0) return;
        _tips.Show(text, this, _tipPoint.X - 8, _tipPoint.Y + 20, TipDurationMs);
    }

    /// <summary>The line a pixel stands for, and the filters that colour it.</summary>
    private string SlotTipText(int slot)
    {
        if (slot < 0 || slot >= _slots || _grid.Document is not { } doc) return "";
        long row = _rowAt[slot];
        long line = doc.RowToLine(row);
        var sb = new StringBuilder();
        sb.Append("Line ").Append((line + 1).ToString("N0"));
        if (_colour[slot] == 0)
        {
            var (from, to) = RowsAt(slot);
            if (to > from + 1) sb.Append('\u2013').Append(doc.RowToLine(to - 1).ToString("N0"));
            return sb.Append("  (nothing matching)").ToString();
        }
        string tip = FilterTipText.Build(doc.FiltersMatching(line));
        return tip.Length == 0 ? sb.ToString() : sb.Append('\n').Append(tip).ToString();
    }
}

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
/// file rather than the whole of it, and the window follows the view. That is the same trade Wireshark makes
/// with its scrollbar overlay, and it is why the scrollbar beside it keeps the whole-file scale.
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
    /// <summary>Logical width. Enough to read a colour and to grab the viewport rectangle.</summary>
    public const int LogicalWidth = 13;

    /// <summary>Unmatched rows skipped per pixel of gap. Only matched rows are worth a pixel each; this is
    /// how much of the space between them the map is prepared to spend.</summary>
    private const int GapRows = 32;

    private const int EdgeWidth = 3;          // device px; marks down the left, find hits down the right
    private const int MarkHeight = 2;
    private const int MinViewportHeight = 8;  // a compressed stretch would otherwise leave it a hairline
    private const int MarginPercent = 15;     // of the window, before it re-centres
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
    private long _builtSelection = -1;
    private (int Top, int Height) _drawnViewport = (-1, -1);

    private bool _dragging;
    private bool _hovering;
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
    public void InvalidateSummary() { _builtGeneration = -1; Invalidate(); }

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
    internal string TipTextForTesting(int slot) => SlotTipText(slot);
    internal void ClickForTesting(int y) => ScrollTo(y);
    internal (int Top, int Height) ViewportForTesting => Geometry();

    /// <summary>How many times the map has actually painted. A picture of it cannot answer that: capturing a
    /// control draws it whether or not it was invalidated, so a screenshot always looks up to date even when
    /// the real window has been sitting stale for minutes.</summary>
    internal int PaintsForTesting => _paints;

    // ---- the window ----

    private int MinRowPixels => Math.Max(1, LogicalToDeviceUnits(1));

    /// <summary>Moves the window so the viewport sits in the middle of it, but only when it has to.
    ///
    /// Left alone the map is a fixed trough with the viewport rectangle sliding down it, which is what makes
    /// it usable as a scrollbar: what you clicked stays where you clicked it. It re-centres in one step when
    /// the viewport approaches an edge, and never moves at all while the pointer is on it - otherwise a drag
    /// would chase itself.</summary>
    private void TrackView(long rows)
    {
        long viewTop = _grid.FirstVisibleRow;
        long viewRows = Math.Max(1, _grid.VisibleRows);
        long margin = Math.Max(1, _span * MarginPercent / 100);
        bool inside = viewTop >= _top + margin && viewTop + viewRows <= _top + _span - margin;
        bool anywhere = viewTop + viewRows > _top && viewTop < _top + _span;
        if (inside || ((_dragging || _hovering) && anywhere)) return;

        long want = viewTop + viewRows / 2 - _span / 2;
        _top = Math.Clamp(want, 0, Math.Max(0, rows - 1));
    }

    private void EnsureSummary()
    {
        var doc = _grid.Document;
        if (doc is null) { _slots = 0; return; }

        long rows = doc.RowCount;
        int height = Math.Max(1, ClientSize.Height);
        int width = Math.Max(1, ClientSize.Width);
        long selection = _grid.SelectionVersion;
        long findHits = doc.FindHitCount;
        if (rows > 0) TrackView(rows);

        if (_builtGeneration == doc.FilterGeneration && _builtRows == rows && _builtTop == _top &&
            _builtHeight == height && _builtWidth == width && _builtFilteredMode == doc.FilteredMode &&
            _builtMarkers == doc.Markers.Version && _builtFindHits == findHits && _builtSelection == selection)
            return;

        _builtGeneration = doc.FilterGeneration;
        _builtRows = rows;
        _builtHeight = height;
        _builtWidth = width;
        _builtFilteredMode = doc.FilteredMode;
        _builtMarkers = doc.Markers.Version;
        _builtFindHits = findHits;
        _builtSelection = selection;

        _rowPixels = MinRowPixels;
        int slots = Math.Max(1, height / _rowPixels);
        // Few enough rows to fit outright: give each one a taller band rather than leaving the map half empty.
        if (rows > 0 && rows <= slots)
        {
            _rowPixels = Math.Max(_rowPixels, height / (int)Math.Max(1, rows));
            slots = Math.Max(1, height / _rowPixels);
        }
        if (_rowAt.Length < slots) { _rowAt = new long[slots]; _colour = new int[slots]; }

        _top = Math.Clamp(_top, 0, Math.Max(0, rows - 1));
        // The window is placed from the span the last build produced, and a build that crosses into a
        // compressed stretch reaches far further than the one before it did. One correcting pass, so the
        // view ends up where the placement meant to put it rather than at an edge.
        for (int pass = 0; ; pass++)
        {
            _slots = rows <= 0 ? 0 : Fill(doc, rows, slots);
            _span = Math.Max(1, (_slots > 0 ? _rowAt[_slots - 1] + 1 : _top + 1) - _top);
            if (pass > 0 || rows <= 0) break;
            long was = _top;
            TrackView(rows);
            _top = Math.Clamp(_top, 0, Math.Max(0, rows - 1));
            if (_top == was) break;
        }
        _builtTop = _top;
        RedrawPicture(width, height);
    }

    /// <summary>Walks rows into pixel slots: one slot per matched row, and one slot per <see cref="GapRows"/>
    /// unmatched rows in between. Returns how many slots were filled.</summary>
    private int Fill(CascadeDocument doc, long rows, int slots)
    {
        var settings = _grid.Settings;
        var defaults = new ResolvedStyle(
            new RgbColor(settings.Foreground.R, settings.Foreground.G, settings.Foreground.B),
            new RgbColor(settings.Background.R, settings.Background.G, settings.Background.B), false, false);

        int at = 0;
        long row = _top;
        while (at < slots && row < rows)
        {
            long line = doc.RowToLine(row);
            long nextLine = doc.NextMatchedLine(line);
            long nextRow = nextLine < 0 ? rows : doc.RowAtOrAfterLine(nextLine);
            if (nextRow > row)
            {
                // Nothing matches between here and there, so the whole stretch is worth a few pixels - at a
                // fixed rate, so that one enormous gap does not quietly rescale the whole map.
                long gap = Math.Min(nextRow, rows) - row;
                int wanted = (int)Math.Max(1, Math.Min(int.MaxValue, gap / GapRows));
                int pixels = Math.Min(wanted, slots - at);
                for (int i = 0; i < pixels; i++)
                {
                    _rowAt[at] = row + gap * i / wanted;
                    _colour[at] = 0;
                    at++;
                }
                if (pixels < wanted) break;   // the map filled up part way through the gap
                row = Math.Min(nextRow, rows);
                continue;
            }
            _rowAt[at] = row;
            _colour[at] = ColourOf(doc, line, defaults, settings);
            at++;
            row++;
        }
        return at;
    }

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

    /// <summary>The slot showing a row, or the nearest one. The rows are non-decreasing across the slots but
    /// not evenly spaced, so this is a search rather than arithmetic.</summary>
    private int SlotOf(long row)
    {
        if (_slots <= 0) return -1;
        if (row <= _rowAt[0]) return 0;
        if (row >= _rowAt[_slots - 1]) return _slots - 1;
        int lo = 0, hi = _slots - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_rowAt[mid] < row) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ---- painting ----

    private void RedrawPicture(int width, int height)
    {
        _picture?.Dispose();
        _picture = null;
        if (_slots <= 0) return;

        int backArgb = _grid.Settings.GutterBack.ToArgb();
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
                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally { picture.UnlockBits(data); }
        _picture = picture;
    }

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
        int edge = Math.Max(1, LogicalToDeviceUnits(1) * EdgeWidth);
        long first = _rowAt[0], last = _rowAt[_slots - 1];

        // Selected rows and find hits share the map with the colours, so they take an edge each rather than
        // covering the row they belong to. The selection goes down first: a mark is deliberate and stays,
        // a selection is wherever you last clicked, and the two want the same three pixels.
        if (_grid.HasSelection)
        {
            using var brush = new SolidBrush(_grid.Settings.SelectionBack);
            for (int s = 0; s < _slots; s++)
                if (_grid.IsRowSelected(_rowAt[s]))
                    g.FillRectangle(brush, 0, s * _rowPixels, edge, Math.Max(MarkHeight, _rowPixels));
        }

        foreach (var (line, mask) in doc.Markers.Snapshot())
        {
            long row = doc.FilteredMode ? doc.RowForLine(line) : line;
            if (row < first || row > last) continue;
            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            using var brush = new SolidBrush(AppSettings.MarkerColors[Math.Clamp(index, 0, AppSettings.MarkerColors.Length - 1)]);
            g.FillRectangle(brush, 0, SlotOf(row) * _rowPixels, edge, Math.Max(MarkHeight, _rowPixels));
        }

        if (doc.FindHitCount > 0)
        {
            using var brush = new SolidBrush(_grid.Settings.FindCurrent);
            int x = Math.Max(0, ClientSize.Width - edge);
            for (int s = 0; s < _slots; s++)
            {
                long from = doc.RowToLine(_rowAt[s]);
                long to = s + 1 < _slots ? doc.RowToLine(_rowAt[s + 1]) : from + 1;
                if (doc.FindHitsInRange(from, Math.Max(from + 1, to)) > 0)
                    g.FillRectangle(brush, x, s * _rowPixels, edge, Math.Max(MarkHeight, _rowPixels));
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
        using (var fill = new SolidBrush(Color.FromArgb(48, settings.SelectionBack)))
            g.FillRectangle(fill, 0, top, ClientSize.Width, height);
        using var pen = new Pen(Color.FromArgb(200, settings.SelectionBack));
        g.DrawRectangle(pen, 0, top, ClientSize.Width - 1, height - 1);
    }

    /// <summary>Repaints when anything the map draws has actually changed. The map is a child control, so the
    /// grid invalidating itself does not touch it - without this the picture would sit exactly as it was last
    /// painted while the text scrolled under it.</summary>
    internal void SyncToGrid()
    {
        if (!Visible || _grid.Document is not { } doc) return;
        if (_builtRows != doc.RowCount || _builtFilteredMode != doc.FilteredMode ||
            _builtGeneration != doc.FilterGeneration || _builtMarkers != doc.Markers.Version ||
            _builtFindHits != doc.FindHitCount || _builtSelection != _grid.SelectionVersion)
        {
            Invalidate();
            return;
        }
        if (_slots <= 0) return;
        long rows = doc.RowCount;
        long before = _top;
        if (rows > 0) TrackView(rows);
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
        ScrollTo(e.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hovering = true;
        if (_dragging) { ScrollTo(e.Y); return; }
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
        Invalidate();   // frozen while the pointer was here; it may have a window to catch up on
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _grid.ScrollByWheel(e.Delta);
    }

    /// <summary>Centres the view on the row under the pointer. The window itself does not move while the
    /// pointer is here, so a row stays where it was clicked and the next click lands where it looks.</summary>
    private void ScrollTo(int y)
    {
        if (_slots <= 0) return;
        int slot = Math.Clamp(y / _rowPixels, 0, _slots - 1);
        _grid.ScrollToRow(_rowAt[slot] - _grid.VisibleRows / 2);
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
            long next = slot + 1 < _slots ? _rowAt[slot + 1] : row + 1;
            if (next > row + 1) sb.Append('\u2013').Append(next.ToString("N0")).Append("  (nothing matching)");
            else sb.Append("  (nothing matching)");
            return sb.ToString();
        }
        string tip = FilterTipText.Build(doc.FiltersMatching(line));
        return tip.Length == 0 ? sb.ToString() : sb.Append('\n').Append(tip).ToString();
    }
}

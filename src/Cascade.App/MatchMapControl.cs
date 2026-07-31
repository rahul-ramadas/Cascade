using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Document;
using Cascade.Core.Filtering;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// A zoomed-out picture of the same thing the vertical scrollbar scrolls through, in the scrollbar's place.
///
/// One pixel row is a <b>band</b> of consecutive display rows. The middle lane draws how much of the band
/// matches (bar length) and what it matches (bar colour); markers tick down the left edge and find hits down
/// the right; the viewport rectangle is the thumb, and the caret is a line. Because the map covers rows and
/// not file lines, it changes with the view mode exactly as the scrollbar does: a sparse constellation over
/// the whole file in dim mode, and a solid colour ribbon in filtered mode, where every row is a match.
///
/// The bar's width is subdivided among the filters present in the band, in proportion to their counts but
/// never below one pixel, allocated in the same deepest-then-topmost priority order the text uses. That is
/// what keeps a single error line visible in a band of seven thousand.
/// </summary>
internal sealed class MatchMapControl : Control
{
    /// <summary>Logical width. Wide enough for the three lanes and to grab the viewport rectangle.</summary>
    public const int LogicalWidth = 20;

    private const int LaneWidth = 3;          // logical; the marker and find lanes
    private const int MinBandPixels = 1;
    private const int TickHeight = 3;         // a single marked line has to be findable at map scale
    private const int MinViewportHeight = 8;  // and the viewport rectangle has to be grabbable

    private readonly LineGridControl _grid;

    private Band[] _bands = Array.Empty<Band>();
    private (long Line, byte Mask)[] _markers = Array.Empty<(long, byte)>();
    private int _builtGeneration = -1;
    private long _builtRows = -1;
    private int _builtHeight = -1;
    private bool _builtFilteredMode;
    private bool _dragging;

    /// <summary>What one pixel row of the map shows: how much of it matched, and the colours in it.</summary>
    private readonly struct Band
    {
        public readonly double Density;
        public readonly Segment[] Segments;
        public Band(double density, Segment[] segments) { Density = density; Segments = segments; }
    }

    private readonly record struct Segment(Color Color, long Count);

    public MatchMapControl(LineGridControl grid)
    {
        _grid = grid;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Right;
        Width = LogicalToDeviceUnits(LogicalWidth);
        TabStop = false;
        AccessibleRole = AccessibleRole.ScrollBar;
        AccessibleName = "Match map";
    }

    /// <summary>Throws away the summary so the next paint rebuilds it. Cheap; the rebuild is the cost.</summary>
    public void InvalidateSummary() { _builtGeneration = -1; Invalidate(); }

    // ---- test seams: the summary itself, rather than the picture of it ----

    internal void RebuildForTesting() { _builtGeneration = -1; EnsureBands(); }
    internal int BandCountForTesting => _bands.Length;
    internal double DensityForTesting(int y) => y >= 0 && y < _bands.Length ? _bands[y].Density : -1;
    internal Color[] ColorsForTesting(int y)
        => y >= 0 && y < _bands.Length ? _bands[y].Segments.Select(s => s.Color).ToArray() : Array.Empty<Color>();

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Width = LogicalToDeviceUnits(LogicalWidth);
    }

    // ---- summary ----

    private void EnsureBands()
    {
        var doc = _grid.Document;
        if (doc is null) { _bands = Array.Empty<Band>(); return; }

        long rows = doc.RowCount;
        int height = Math.Max(1, ClientSize.Height);
        if (_builtGeneration == doc.FilterGeneration && _builtRows == rows &&
            _builtHeight == height && _builtFilteredMode == doc.FilteredMode && _bands.Length == height)
            return;

        _builtGeneration = doc.FilterGeneration;
        _builtRows = rows;
        _builtHeight = height;
        _builtFilteredMode = doc.FilteredMode;
        _markers = doc.Markers.Snapshot();

        var bands = new Band[height];
        if (rows <= 0) { _bands = bands; return; }

        var palette = BuildPalette(doc);
        var counts = new long[palette.Count];

        for (int y = 0; y < height; y++)
        {
            long rowFrom = (long)((double)y * rows / height);
            long rowTo = (long)((double)(y + 1) * rows / height);
            if (rowTo <= rowFrom) rowTo = rowFrom + 1;
            long bandRows = rowTo - rowFrom;

            (long lineFrom, long lineTo) = LineRange(doc, rowFrom, rowTo, rows);
            long matched = doc.MatchedLinesInRange(lineFrom, lineTo);
            // Measured in ROWS, not lines: in filtered mode every row is a match, so this is 1 and the bar
            // runs the full width, while in dim mode rows are lines and it is the local match density.
            double density = Math.Clamp((double)matched / bandRows, 0, 1);

            Segment[] segments = Array.Empty<Segment>();
            if (matched > 0 && palette.Count > 0)
            {
                int used = 0;
                for (int i = 0; i < palette.Count; i++)
                {
                    counts[i] = palette[i].Set.CountInRange(lineFrom, lineTo);
                    if (counts[i] > 0) used++;
                }
                if (used > 0)
                {
                    segments = new Segment[used];
                    int at = 0;
                    for (int i = 0; i < palette.Count; i++)
                        if (counts[i] > 0) segments[at++] = new Segment(palette[i].Color, counts[i]);
                }
            }
            bands[y] = new Band(density, segments);
        }
        _bands = bands;
    }

    /// <summary>The file lines a band of rows covers. In dim mode a row is a line; in filtered mode the
    /// band's rows have to be resolved back to the lines showing in them.</summary>
    private static (long From, long To) LineRange(CascadeDocument doc, long rowFrom, long rowTo, long rows)
    {
        if (!doc.FilteredMode) return (rowFrom, rowTo);
        long from = doc.RowToLine(rowFrom);
        long to = rowTo >= rows ? doc.CompletedLineCount : doc.RowToLine(rowTo);
        return (from, Math.Max(from + 1, to));
    }

    /// <summary>The enabled include filters that have a cached line set, in the order the text uses to pick a
    /// line's colour: deepest first, then topmost. A filter with no set (nothing cached for it yet) is left
    /// out, and the band falls back to whatever else is there.</summary>
    private List<(FilterMatchCache.MatchSet Set, Color Color)> BuildPalette(CascadeDocument doc)
    {
        var result = new List<(FilterMatchCache.MatchSet, Color, int Depth, int Order)>();
        int order = 0;
        foreach (var f in doc.Filters.EnumerateDepthFirst())
        {
            int at = order++;
            if (!f.Enabled || f.Kind != FilterKind.Include) continue;
            if (doc.MatchSetFor(f) is not { } set) continue;
            result.Add((set, ColorFor(f), f.Depth, at));
        }
        result.Sort((a, b) => a.Depth != b.Depth ? b.Depth - a.Depth : a.Order - b.Order);
        return result.Select(r => (r.Item1, r.Item2)).ToList();
    }

    /// <summary>The single colour that best stands for a line this filter colours: its background when it
    /// resolves one (that is what fills the row), otherwise its text colour.</summary>
    private Color ColorFor(Filter f)
    {
        var settings = _grid.Settings;
        var defaults = new ResolvedStyle(
            new RgbColor(settings.Foreground.R, settings.Foreground.G, settings.Foreground.B),
            new RgbColor(settings.Background.R, settings.Background.G, settings.Background.B), false, false);
        var style = StyleResolver.Resolve(f, defaults);
        var bg = Color.FromArgb(style.Background.R, style.Background.G, style.Background.B);
        return bg != settings.Background ? bg : Color.FromArgb(style.Foreground.R, style.Foreground.G, style.Foreground.B);
    }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var settings = _grid.Settings;
        g.Clear(settings.GutterBack);
        EnsureBands();

        var doc = _grid.Document;
        if (doc is null || _bands.Length == 0) return;

        int lane = LogicalToDeviceUnits(LaneWidth);
        int barLeft = lane;
        int barWidth = Math.Max(1, ClientSize.Width - lane * 2);
        long rows = Math.Max(1, doc.RowCount);
        int height = _bands.Length;

        for (int y = 0; y < height; y++)
        {
            var band = _bands[y];
            if (band.Density <= 0) continue;
            int total = Math.Max(MinBandPixels, (int)Math.Round(barWidth * band.Density));
            total = Math.Min(total, barWidth);
            DrawBandBar(g, band, barLeft, y, total);
        }

        DrawTicks(g, doc, rows, height, 0, lane, marker: true);
        DrawViewport(g, doc, rows, height);
    }

    /// <summary>Fills a band's bar, giving every filter present at least one pixel and the rest in
    /// proportion, highest priority first. When the bar is a single pixel that means the most important
    /// colour in the band wins it, which is the whole point at one pixel per seven thousand lines.</summary>
    private static void DrawBandBar(Graphics g, Band band, int left, int y, int width)
    {
        if (band.Segments.Length == 0)
        {
            using var plain = new SolidBrush(Color.FromArgb(150, 150, 150));
            g.FillRectangle(plain, left, y, width, 1);
            return;
        }

        long sum = 0;
        foreach (var s in band.Segments) sum += s.Count;
        int x = left, remaining = width;
        for (int i = 0; i < band.Segments.Length && remaining > 0; i++)
        {
            int take = i == band.Segments.Length - 1
                ? remaining
                : Math.Clamp((int)Math.Round(width * (double)band.Segments[i].Count / sum), 1, remaining);
            // Leave a pixel for each filter still to come, so a rare high-priority colour is never squeezed
            // out by a common one that happens to be next in line.
            take = Math.Min(take, remaining - (band.Segments.Length - 1 - i));
            if (take <= 0) take = 1;
            using var brush = new SolidBrush(band.Segments[i].Color);
            g.FillRectangle(brush, x, y, take, 1);
            x += take;
            remaining -= take;
        }
    }

    private void DrawTicks(Graphics g, CascadeDocument doc, long rows, int height, int x, int width, bool marker)
    {
        if (!marker || _markers.Length == 0) return;
        foreach (var (line, mask) in _markers)
        {
            long row = doc.FilteredMode ? doc.RowForLine(line) : line;
            if (row < 0) continue;
            int y = (int)(row * height / rows);
            if (y < 0 || y >= height) continue;
            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            using var brush = new SolidBrush(AppSettings.MarkerColors[Math.Clamp(index, 0, AppSettings.MarkerColors.Length - 1)]);
            g.FillRectangle(brush, x, Math.Min(y, height - TickHeight), width, TickHeight);
        }
    }

    private void DrawViewport(Graphics g, CascadeDocument doc, long rows, int height)
    {
        long first = _grid.FirstVisibleRow;
        int visible = _grid.VisibleRows;
        int top = (int)(first * height / rows);
        int bottom = (int)Math.Min(height, ((first + visible) * height + rows - 1) / rows);
        int h = Math.Max(MinViewportHeight, bottom - top);
        if (top + h > height) top = Math.Max(0, height - h);

        var settings = _grid.Settings;
        using (var fill = new SolidBrush(Color.FromArgb(48, settings.SelectionBack)))
            g.FillRectangle(fill, 0, top, ClientSize.Width, h);
        using (var pen = new Pen(Color.FromArgb(200, settings.SelectionBack)))
            g.DrawRectangle(pen, 0, top, ClientSize.Width - 1, h - 1);

        long caret = _grid.CaretRow;
        if (caret >= 0 && rows > 0)
        {
            int y = (int)(caret * height / rows);
            using var pen = new Pen(settings.SelectionBack);
            g.DrawLine(pen, 0, y, ClientSize.Width, y);
        }
    }

    // ---- interaction ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        ScrollTo(e.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) ScrollTo(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _grid.ScrollByWheel(e.Delta);
    }

    /// <summary>Centres the viewport on the band under the pointer, which is what a click on a map means.</summary>
    private void ScrollTo(int y)
    {
        var doc = _grid.Document;
        if (doc is null) return;
        long rows = doc.RowCount;
        if (rows <= 0) return;
        int height = Math.Max(1, ClientSize.Height);
        long row = (long)((double)Math.Clamp(y, 0, height - 1) * rows / height);
        _grid.ScrollToRow(row - _grid.VisibleRows / 2);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new MapAccessibleObject(this);

    /// <summary>The map replaces the vertical scrollbar, so it has to answer for it: assistive technology
    /// and the UI tests both scroll a view by setting a scrollbar's value.</summary>
    private sealed class MapAccessibleObject : Control.ControlAccessibleObject
    {
        private readonly MatchMapControl _map;

        public MapAccessibleObject(MatchMapControl map) : base(map) => _map = map;

        public override AccessibleRole Role => AccessibleRole.ScrollBar;

        public override string? Value
        {
            get => (_map._grid.FirstVisibleRow).ToString();
            set { if (long.TryParse(value, out long row)) _map._grid.ScrollToRow(row); }
        }
    }
}

using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Document;
using Cascade.Core.Filtering;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// A zoomed-out picture of the same thing the vertical scrollbar scrolls through, in the scrollbar's place.
///
/// One pixel row is a <b>band</b> of consecutive display rows. The middle of the map is divided into a
/// <b>lane per filter</b> - one for each enabled filter that no enabled filter above it already covers - laid
/// out left to right in list order, so the map reads like the filter list turned on its side. A lane pixel is
/// painted when that filter has lines in that band, more strongly the more of the band it accounts for. That
/// is what makes a filter's <i>distribution</i> visible: a burst of errors is four bright marks in the error
/// lane, and a filter that runs all the way through is a lane that never breaks.
///
/// Markers tick down the left edge and find hits down the right; the viewport rectangle is the thumb, and the
/// caret is a line. The lanes are drawn once into a bitmap when the summary is rebuilt, so scrolling costs
/// only a blit - which is what lets the rectangle follow the view at all.
///
/// It was previously a single bar whose length was the band's match density, subdivided among every filter in
/// proportion to its share. On real data that said almost nothing: across a whole file the mix in one band of
/// twenty thousand lines is much like the mix in the next, so the picture was the same fixed run of stripes
/// from top to bottom, and the length was either a permanent one-pixel line (a rare filter) or a permanent
/// block (a common one).
/// </summary>
internal sealed class MatchMapControl : Control
{
    /// <summary>Logical width. Wide enough for the lanes and to grab the viewport rectangle.</summary>
    public const int LogicalWidth = 20;

    private const int EdgeLane = 2;           // logical; the marker and find lanes down the two edges
    private const int MinLanePixels = 2;      // below this a lane is a hairline, not a lane
    private const int TickHeight = 3;         // a single marked line has to be findable at map scale
    private const int MinViewportHeight = 8;  // and the viewport rectangle has to be grabbable
    private const int HoverDelayMs = 400;
    private const int TipDurationMs = 8000;

    private readonly LineGridControl _grid;
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = HoverDelayMs };

    private Band[] _bands = Array.Empty<Band>();
    private MapLanes.Lane[] _lanes = Array.Empty<MapLanes.Lane>();
    private float[] _lanePeak = Array.Empty<float>();
    private (long Line, byte Mask)[] _markers = Array.Empty<(long, byte)>();
    private Bitmap? _picture;
    private int _builtGeneration = -1;
    private long _builtRows = -1;
    private int _builtHeight = -1;
    private int _builtWidth = -1;
    private bool _builtFilteredMode;
    private int _builtMarkers = -1;
    private long _builtFindHits = -1;
    private (int Top, int Height, int Caret) _drawnViewport = (-1, -1, -1);
    private bool _dragging;
    private int _tipBand = -1;
    private Point _tipPoint;
    private int _paints;

    /// <summary>What one pixel row of the map shows: how much of it matched at all, how much of it each lane
    /// accounts for, and whether the current find term is anywhere in it.</summary>
    private readonly struct Band
    {
        public readonly double Density;
        public readonly float[] Shares;
        public readonly bool Find;
        public Band(double density, float[] shares, bool find) { Density = density; Shares = shares; Find = find; }
    }

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
        _tipTimer.Tick += (_, _) => ShowTipNow();
    }

    protected override void Dispose(bool disposing)
    {
        // None of these is a child control, so nothing else would clean them up.
        if (disposing) { _tipTimer.Dispose(); _tips.Dispose(); _picture?.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Throws away the summary so the next paint rebuilds it. Cheap; the rebuild is the cost.</summary>
    public void InvalidateSummary() { _builtGeneration = -1; Invalidate(); }

    // ---- test seams: the summary itself, rather than the picture of it ----

    internal void RebuildForTesting() { _builtGeneration = -1; EnsureBands(); }
    internal int BandCountForTesting => _bands.Length;
    internal double DensityForTesting(int y) => y >= 0 && y < _bands.Length ? _bands[y].Density : -1;
    internal int LaneCountForTesting => _lanes.Length;
    internal string[] LaneNamesForTesting => _lanes.Select(l => l.Filter.DisplayName).ToArray();
    internal Color[] LaneColorsForTesting => _lanes.Select(l => l.Color).ToArray();
    internal bool LanesFitForTesting => _lanes.Length > 0 && BarWidth / _lanes.Length >= MinLanePixels;
    internal double ShareForTesting(int y, int lane)
        => y >= 0 && y < _bands.Length && lane >= 0 && lane < _bands[y].Shares.Length ? _bands[y].Shares[lane] : -1;
    internal bool FindInBandForTesting(int y) => y >= 0 && y < _bands.Length && _bands[y].Find;
    internal double LanePeakForTesting(int lane) => lane >= 0 && lane < _lanePeak.Length ? _lanePeak[lane] : -1;
    internal string TipTextForTesting(int band) => BandTipText(band);
    internal string TipOverLaneForTesting(int band, int lane) => BandTipText(band, lane);

    /// <summary>Which lane the pointer would be over at this x, so a test can check the same arithmetic the
    /// tip uses rather than a copy of it.</summary>
    internal int LaneAtForTesting(int x) => LaneAt(x);

    internal (int Left, int Width) BarBoundsForTesting => (BarLeft, BarWidth);

    /// <summary>How many times the map has actually painted. A picture of it cannot answer that: capturing
    /// a control draws it whether or not it was invalidated, so a screenshot always looks up to date even
    /// when the real window has been sitting stale for minutes.</summary>
    internal int PaintsForTesting => _paints;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Width = LogicalToDeviceUnits(LogicalWidth);
    }

    // ---- summary ----

    private int EdgeWidth => Math.Max(1, LogicalToDeviceUnits(EdgeLane));
    private int BarLeft => EdgeWidth;
    private int BarWidth => Math.Max(1, ClientSize.Width - EdgeWidth * 2);

    private void EnsureBands()
    {
        var doc = _grid.Document;
        if (doc is null) { _bands = Array.Empty<Band>(); _lanes = Array.Empty<MapLanes.Lane>(); return; }

        long rows = doc.RowCount;
        int height = Math.Max(1, ClientSize.Height);
        int width = Math.Max(1, ClientSize.Width);
        long findHits = doc.FindHitCount;
        if (_builtGeneration == doc.FilterGeneration && _builtRows == rows &&
            _builtHeight == height && _builtWidth == width && _builtFilteredMode == doc.FilteredMode &&
            _builtMarkers == doc.Markers.Version && _builtFindHits == findHits && _bands.Length == height)
            return;

        _builtGeneration = doc.FilterGeneration;
        _builtRows = rows;
        _builtHeight = height;
        _builtWidth = width;
        _builtFilteredMode = doc.FilteredMode;
        _builtMarkers = doc.Markers.Version;
        _builtFindHits = findHits;
        _markers = doc.Markers.Snapshot();
        _lanes = MapLanes.For(doc, _grid.Settings).ToArray();
        _lanePeak = new float[_lanes.Length];

        var bands = new Band[height];
        var empty = Array.Empty<float>();
        // Filled rather than left default: a default band has no share array at all, and the view really can
        // have no rows - at startup, and whenever the filters in force match nothing.
        Array.Fill(bands, new Band(0, empty, false));
        _bands = bands;
        if (rows <= 0) { RedrawPicture(); return; }

        int n = _lanes.Length;
        for (int y = 0; y < height; y++)
        {
            long rowFrom = (long)((double)y * rows / height);
            long rowTo = (long)((double)(y + 1) * rows / height);
            if (rowTo <= rowFrom) rowTo = rowFrom + 1;
            long bandRows = rowTo - rowFrom;

            (long lineFrom, long lineTo) = LineRange(doc, rowFrom, rowTo, rows);
            long matched = doc.MatchedLinesInRange(lineFrom, lineTo);
            // Measured against ROWS, not the file lines the band spans: in filtered mode every row is a
            // match, so this is 1, while in dim mode rows are lines and it is the local match density.
            double density = Math.Clamp((double)matched / bandRows, 0, 1);

            float[] shares = empty;
            if (n > 0)
            {
                shares = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float share = (float)Math.Clamp((double)_lanes[i].Set.CountInRange(lineFrom, lineTo) / bandRows, 0, 1);
                    shares[i] = share;
                    if (share > _lanePeak[i]) _lanePeak[i] = share;
                }
            }
            bands[y] = new Band(density, shares, findHits > 0 && doc.FindHitsInRange(lineFrom, lineTo) > 0);
        }
        RedrawPicture();
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

    /// <summary>Blends a lane's colour over the gutter, against the busiest band that lane has anywhere in
    /// the file rather than against the whole band.
    ///
    /// The question a map answers is where a filter's lines are, not what proportion of the log they are;
    /// measured absolutely, a filter holding half a percent of a huge file is the same faint wash from top to
    /// bottom and says nothing at all. Scaled against its own peak, its busiest stretches stand out and its
    /// quiet ones recede. It never fades below half strength, because at this scale a band holding one line
    /// in twenty thousand is exactly the thing worth seeing.</summary>
    private static Color Strength(Color colour, Color back, double share, double peak)
    {
        double t = 0.5 + 0.5 * Math.Sqrt(Math.Clamp(peak > 0 ? share / peak : 0, 0, 1));
        return Color.FromArgb(
            (int)Math.Round(back.R + (colour.R - back.R) * t),
            (int)Math.Round(back.G + (colour.G - back.G) * t),
            (int)Math.Round(back.B + (colour.B - back.B) * t));
    }

    // ---- painting ----

    /// <summary>Draws the lanes into an off-screen strip. A repaint then costs one blit instead of several
    /// thousand rectangles, which is what lets the viewport rectangle follow the view as it scrolls.</summary>
    private void RedrawPicture()
    {
        _picture?.Dispose();
        _picture = null;
        int height = _bands.Length, width = Math.Max(1, ClientSize.Width);
        if (height == 0) return;

        var back = _grid.Settings.GutterBack;
        int backArgb = back.ToArgb();
        int findArgb = _grid.Settings.FindCurrent.ToArgb();
        int barLeft = BarLeft, barWidth = BarWidth, n = _lanes.Length;
        bool lanes = n > 0 && barWidth / n >= MinLanePixels;
        int edge = EdgeWidth;

        var picture = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = picture.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[width];
            for (int y = 0; y < height; y++)
            {
                Array.Fill(row, backArgb);
                var band = _bands[y];
                if (lanes) DrawLanes(row, band, barLeft, barWidth, n, back);
                else DrawDensity(row, band, barLeft, barWidth);
                if (band.Find)
                    for (int x = Math.Max(0, width - edge); x < width; x++) row[x] = findArgb;
                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally { picture.UnlockBits(data); }
        _picture = picture;
    }

    private void DrawLanes(int[] row, Band band, int barLeft, int barWidth, int n, Color back)
    {
        for (int i = 0; i < n; i++)
        {
            double share = i < band.Shares.Length ? band.Shares[i] : 0;
            if (share <= 0) continue;
            int from = barLeft + (int)((long)i * barWidth / n);
            int to = barLeft + (int)((long)(i + 1) * barWidth / n);
            int argb = Strength(_lanes[i].Color, back, share, _lanePeak[i]).ToArgb();
            for (int x = from; x < to && x < row.Length; x++) row[x] = argb;
        }
    }

    /// <summary>What the map falls back to when more filters are on than there is room to separate: one bar,
    /// as long as the band's match density, in the colour of whichever lane leads it. Square-rooted because
    /// at whole-file scale most bands are a percent or two, and a linear bar would be a hairline all the way
    /// down - which is precisely what the map used to be.</summary>
    private void DrawDensity(int[] row, Band band, int barLeft, int barWidth)
    {
        if (band.Density <= 0) return;
        int width = Math.Clamp((int)Math.Round(barWidth * Math.Sqrt(band.Density)), 2, barWidth);
        Color colour = _grid.Settings.DimForeground;
        double best = 0;
        for (int i = 0; i < band.Shares.Length && i < _lanes.Length; i++)
            if (band.Shares[i] > best) { best = band.Shares[i]; colour = _lanes[i].Color; }
        int argb = colour.ToArgb();
        for (int x = barLeft; x < barLeft + width && x < row.Length; x++) row[x] = argb;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _paints++;
        var g = e.Graphics;
        var settings = _grid.Settings;
        g.Clear(settings.GutterBack);
        EnsureBands();

        var doc = _grid.Document;
        if (doc is null || _bands.Length == 0) return;
        if (_picture is { } picture) g.DrawImageUnscaled(picture, 0, 0);

        long rows = Math.Max(1, doc.RowCount);
        DrawMarkers(g, doc, rows, _bands.Length);
        DrawViewport(g, doc, rows, _bands.Length);
    }

    private void DrawMarkers(Graphics g, CascadeDocument doc, long rows, int height)
    {
        if (_markers.Length == 0) return;
        int width = EdgeWidth;
        foreach (var (line, mask) in _markers)
        {
            long row = doc.FilteredMode ? doc.RowForLine(line) : line;
            if (row < 0) continue;
            int y = (int)(row * height / rows);
            if (y < 0 || y >= height) continue;
            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            using var brush = new SolidBrush(AppSettings.MarkerColors[Math.Clamp(index, 0, AppSettings.MarkerColors.Length - 1)]);
            g.FillRectangle(brush, 0, Math.Min(y, height - TickHeight), width, TickHeight);
        }
    }

    private void DrawViewport(Graphics g, CascadeDocument doc, long rows, int height)
    {
        var (top, h, caretY) = ViewportGeometry(rows, height);
        _drawnViewport = (top, h, caretY);

        var settings = _grid.Settings;
        using (var fill = new SolidBrush(Color.FromArgb(48, settings.SelectionBack)))
            g.FillRectangle(fill, 0, top, ClientSize.Width, h);
        using (var pen = new Pen(Color.FromArgb(200, settings.SelectionBack)))
            g.DrawRectangle(pen, 0, top, ClientSize.Width - 1, h - 1);

        if (caretY >= 0)
        {
            using var pen = new Pen(settings.SelectionBack);
            g.DrawLine(pen, 0, caretY, ClientSize.Width, caretY);
        }
    }

    private (int Top, int Height, int Caret) ViewportGeometry(long rows, int height)
    {
        long first = _grid.FirstVisibleRow;
        int visible = _grid.VisibleRows;
        int top = (int)(first * height / rows);
        int bottom = (int)Math.Min(height, ((first + visible) * height + rows - 1) / rows);
        int h = Math.Max(MinViewportHeight, bottom - top);
        if (top + h > height) top = Math.Max(0, height - h);
        long caret = _grid.CaretRow;
        return (top, h, caret >= 0 ? (int)(caret * height / rows) : -1);
    }

    /// <summary>Repaints when anything the map draws has actually changed.
    ///
    /// The map is a child control standing in for the scrollbar, so the grid invalidating itself does not
    /// touch it: without this the picture stays exactly as it was last painted while the text scrolls under
    /// it and - worse - while the view switches between showing every line and only the matching ones, which
    /// is an entirely different map. Cheap enough to hang off every invalidation the grid makes, because
    /// when nothing has moved it does nothing.</summary>
    internal void SyncToGrid()
    {
        if (!Visible || _grid.Document is not { } doc) return;
        if (_builtRows != doc.RowCount || _builtFilteredMode != doc.FilteredMode ||
            _builtGeneration != doc.FilterGeneration || _builtMarkers != doc.Markers.Version ||
            _builtFindHits != doc.FindHitCount)
        {
            Invalidate();
            return;
        }
        if (_bands.Length == 0) return;
        if (ViewportGeometry(Math.Max(1, doc.RowCount), _bands.Length) != _drawnViewport) Invalidate();
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
        if (_dragging) { ScrollTo(e.Y); return; }
        TrackHover(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        HideTip();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _grid.ScrollByWheel(e.Delta);
    }

    // ---- what is under the pointer ----

    /// <summary>Restarts the hover countdown whenever the pointer moves to a different band, so the tip
    /// describes where it settled rather than everywhere it passed through.</summary>
    private void TrackHover(Point at)
    {
        if (at.Y < 0 || at.Y >= _bands.Length) { HideTip(); return; }
        if (at.Y == _tipBand) return;
        HideTip();
        _tipBand = at.Y;
        _tipPoint = at;
        _tipTimer.Stop();
        _tipTimer.Start();
    }

    private void HideTip()
    {
        _tipTimer.Stop();
        if (_tipBand >= 0) _tips.Hide(this);
        _tipBand = -1;
    }

    private void ShowTipNow()
    {
        _tipTimer.Stop();
        string text = BandTipText(_tipBand, LaneAt(_tipPoint.X));
        if (text.Length == 0) return;
        _tips.Show(text, this, _tipPoint.X - 8, _tipPoint.Y + 20, TipDurationMs);
    }

    /// <summary>Which lane a given x is over, or -1 for the edges and the gutter.</summary>
    private int LaneAt(int x)
    {
        int n = _lanes.Length, barLeft = BarLeft, barWidth = BarWidth;
        if (n == 0 || barWidth / n < MinLanePixels || x < barLeft || x >= barLeft + barWidth) return -1;
        return Math.Clamp((x - barLeft) * n / barWidth, 0, n - 1);
    }

    /// <summary>What a band holds, in words, naming the lane under the pointer first.
    ///
    /// Without this the lanes are colours with nothing to read them against. The key beside each filter in
    /// the list answers "which lane is that filter"; this answers the other direction - "whose lane am I
    /// pointing at" - which is the one you ask when something unexpected shows up on the map.</summary>
    private string BandTipText(int band, int lane = -1)
    {
        if (band < 0 || band >= _bands.Length || _grid.Document is not { } doc) return "";
        long rows = doc.RowCount;
        if (rows <= 0) return "";
        long rowFrom = (long)((double)band * rows / _bands.Length);
        long rowTo = Math.Max(rowFrom + 1, (long)((double)(band + 1) * rows / _bands.Length));
        (long lineFrom, long lineTo) = LineRange(doc, rowFrom, rowTo, rows);

        var sb = new StringBuilder();
        if (lane >= 0 && lane < _lanes.Length)
            sb.Append("This lane: ").Append(Trim(_lanes[lane].Filter.DisplayName)).Append('\n');
        sb.Append(doc.FilteredMode ? "Rows " : "Lines ")
          .Append((rowFrom + 1).ToString("N0")).Append('\u2013').Append(rowTo.ToString("N0"));
        if (doc.FilteredMode)
            sb.Append(" (lines ").Append((lineFrom + 1).ToString("N0")).Append('\u2013').Append(lineTo.ToString("N0")).Append(')');

        var named = new List<(string Name, long Count)>();
        foreach (var l in _lanes)
        {
            long c = l.Set.CountInRange(lineFrom, lineTo);
            if (c > 0) named.Add((l.Filter.DisplayName, c));
        }
        if (named.Count == 0) return sb.Append("\nnothing here matches").ToString();
        foreach (var (name, count) in named.OrderByDescending(x => x.Count).Take(8))
            sb.Append('\n').Append(count.ToString("N0")).Append("  ").Append(Trim(name));
        return sb.ToString();
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..57] + "\u2026";

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

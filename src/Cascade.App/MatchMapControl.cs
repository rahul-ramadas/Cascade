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
    private const int MaxLanes = 24;          // beyond this there is nothing to see; fall back to density
    private const int TickHeight = 3;         // a single marked line has to be findable at map scale
    private const int MinViewportHeight = 8;  // and the viewport rectangle has to be grabbable
    private const int HoverDelayMs = 400;
    private const int TipDurationMs = 8000;

    private readonly LineGridControl _grid;
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = HoverDelayMs };

    private Band[] _bands = Array.Empty<Band>();
    private Lane[] _lanes = Array.Empty<Lane>();
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

    /// <summary>One filter's column on the map.</summary>
    private readonly record struct Lane(string Name, Color Color, FilterMatchCache.MatchSet Set);

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
    internal string[] LaneNamesForTesting => _lanes.Select(l => l.Name).ToArray();
    internal Color[] LaneColorsForTesting => _lanes.Select(l => l.Color).ToArray();
    internal bool LanesFitForTesting => _lanes.Length > 0 && BarWidth / _lanes.Length >= MinLanePixels;
    internal double ShareForTesting(int y, int lane)
        => y >= 0 && y < _bands.Length && lane >= 0 && lane < _bands[y].Shares.Length ? _bands[y].Shares[lane] : -1;
    internal bool FindInBandForTesting(int y) => y >= 0 && y < _bands.Length && _bands[y].Find;
    internal double LanePeakForTesting(int lane) => lane >= 0 && lane < _lanePeak.Length ? _lanePeak[lane] : -1;
    internal string TipTextForTesting(int band) => BandTipText(band);

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
        if (doc is null) { _bands = Array.Empty<Band>(); _lanes = Array.Empty<Lane>(); return; }

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
        _lanes = BuildLanes(doc);
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

    /// <summary>One lane per enabled include filter that no enabled include filter above it already covers.
    ///
    /// Nesting narrows, so a filter's lines are a subset of its enabled parent's - the parent's lane already
    /// accounts for every one of them, and giving the children lanes of their own would say the same thing
    /// several times over in ever-thinner columns. Turning on one filter with twenty children under it is one
    /// lane; turning on six unrelated filters is six.</summary>
    private Lane[] BuildLanes(CascadeDocument doc)
    {
        var result = new List<Lane>();
        foreach (var f in doc.Filters.EnumerateDepthFirst())
        {
            if (!f.Enabled || f.Kind != FilterKind.Include) continue;
            if (HasEnabledIncludeAncestor(f)) continue;
            // A filter that matches nothing gets no lane. It would be a blank column taking width off the
            // ones that have something to show, and there are usually several of them: a saved filter set
            // carries filters for every log this person reads, not just this one.
            if (doc.MatchCountFor(f) == 0) continue;
            if (doc.MatchSetFor(f) is not { } set) continue;
            result.Add(new Lane(f.DisplayName, MapColor(f, result.Count), set));
            if (result.Count >= MaxLanes) break;
        }
        return result.ToArray();
    }

    private static bool HasEnabledIncludeAncestor(Filter f)
    {
        for (var p = f.Parent; p is not null; p = p.Parent)
            if (p.Enabled && p.Kind == FilterKind.Include) return true;
        return false;
    }

    // ---- colour ----

    /// <summary>Colours for filters that have none of their own, spaced round the wheel so that neighbouring
    /// lanes never read as the same colour.</summary>
    private static readonly Color[] Fallback =
    {
        Color.FromArgb(0xE6, 0x39, 0x46), Color.FromArgb(0x1D, 0x7D, 0xD8), Color.FromArgb(0x2A, 0x9D, 0x54),
        Color.FromArgb(0xE8, 0x8B, 0x0A), Color.FromArgb(0x8E, 0x44, 0xCC), Color.FromArgb(0x00, 0x9A, 0xA6),
        Color.FromArgb(0xC2, 0x2E, 0x8A), Color.FromArgb(0x7A, 0x6A, 0x00),
    };

    internal static Color FallbackForTesting(int index) => Fallback[index % Fallback.Length];

    /// <summary>The colour that stands for this filter on the map.
    ///
    /// Its own, when it has one - a lane you can match to the rows it colours is worth a great deal - but
    /// pushed until it actually reads a few pixels wide against the gutter. Row colours are picked to be
    /// quiet enough to put text on, which is exactly what makes them vanish here: a pale grey highlight and
    /// a pale blue one are the same pixel. A filter with no colour of its own takes one from the palette
    /// rather than the text colour, which every unstyled filter would share.</summary>
    private Color MapColor(Filter f, int index)
    {
        var settings = _grid.Settings;
        var defaults = new ResolvedStyle(
            new RgbColor(settings.Foreground.R, settings.Foreground.G, settings.Foreground.B),
            new RgbColor(settings.Background.R, settings.Background.G, settings.Background.B), false, false);
        var style = StyleResolver.Resolve(f, defaults);
        var bg = Color.FromArgb(style.Background.R, style.Background.G, style.Background.B);
        var fg = Color.FromArgb(style.Foreground.R, style.Foreground.G, style.Foreground.B);

        Color own = bg.ToArgb() != settings.Background.ToArgb() ? bg
                  : fg.ToArgb() != settings.Foreground.ToArgb() ? fg
                  : Color.Empty;
        // A grey, black or white highlight has no hue to keep, and forcing saturation onto one invents a
        // colour outright - a filter styled black on yellow came out as a red lane. Those take a palette
        // colour too: a grey lane against a grey gutter would be nothing to look at either way.
        if (own.IsEmpty || ToHsl(own).S < 0.15) return Fallback[index % Fallback.Length];
        return Vivid(own, settings.GutterBack);
    }

    /// <summary>Raises a colour's saturation and pulls its lightness away from <paramref name="against"/>,
    /// keeping its hue. Enough to tell two pastels apart in a three-pixel column.</summary>
    internal static Color Vivid(Color c, Color against)
    {
        (double h, double s, double l) = ToHsl(c);
        s = Math.Max(s, 0.65);
        double target = Luminance(against);
        // Away from the background in whichever direction there is room, so this works the same on a dark
        // theme as on a light one.
        if (Math.Abs(l - target) < 0.30) l = target > 0.5 ? target - 0.30 : target + 0.30;
        return FromHsl(h, s, Math.Clamp(l, 0.18, 0.82));
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        if (max > min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0) { int v = (int)Math.Round(l * 255); return Color.FromArgb(v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Color.FromArgb(Channel(p, q, h + 1.0 / 3), Channel(p, q, h), Channel(p, q, h - 1.0 / 3));
    }

    private static int Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        double v = t < 1.0 / 6 ? p + (q - p) * 6 * t
                 : t < 1.0 / 2 ? q
                 : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
                 : p;
        return (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
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
        string text = BandTipText(_tipBand);
        if (text.Length == 0) return;
        _tips.Show(text, this, _tipPoint.X - 8, _tipPoint.Y + 20, TipDurationMs);
    }

    /// <summary>What a band holds, in words. Without this the lanes are colours with nothing to read them
    /// against: the filter list is right there, but which lane is which would otherwise be a guess.</summary>
    private string BandTipText(int band)
    {
        if (band < 0 || band >= _bands.Length || _grid.Document is not { } doc) return "";
        long rows = doc.RowCount;
        if (rows <= 0) return "";
        long rowFrom = (long)((double)band * rows / _bands.Length);
        long rowTo = Math.Max(rowFrom + 1, (long)((double)(band + 1) * rows / _bands.Length));
        (long lineFrom, long lineTo) = LineRange(doc, rowFrom, rowTo, rows);

        var sb = new StringBuilder();
        sb.Append(doc.FilteredMode ? "Rows " : "Lines ")
          .Append((rowFrom + 1).ToString("N0")).Append('\u2013').Append(rowTo.ToString("N0"));
        if (doc.FilteredMode)
            sb.Append(" (lines ").Append((lineFrom + 1).ToString("N0")).Append('\u2013').Append(lineTo.ToString("N0")).Append(')');

        var named = new List<(string Name, long Count)>();
        foreach (var lane in _lanes)
        {
            long c = lane.Set.CountInRange(lineFrom, lineTo);
            if (c > 0) named.Add((lane.Name, c));
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

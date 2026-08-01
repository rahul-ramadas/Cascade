using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>
/// The vertical scrollbar, drawn rather than borrowed: sunk into a trough of its own so it does not read as
/// more of the minimap beside it, with a rounded grey thumb rather than the map's square blue rectangle, and
/// with the marked lines of the whole file ticked down it.
///
/// The trough is the only place those marks can go. The minimap shows a window of a few hundred rows, so a
/// mark outside that window has nowhere to appear - and "where are my marks in this file" is exactly the
/// question the scrollbar's own scale answers.
/// </summary>
internal sealed class SlimScrollBar : Control
{
    public const int LogicalWidth = 14;

    private const int MarkHeight = 2;      // device px; a single marked line has to be findable at file scale
    private const int MinThumbHeight = 16;

    private readonly LineGridControl _grid;
    private long _value;
    private long _rows;
    private long _visible = 1;
    private bool _dragging;
    private bool _hot;
    private int _grabOffset;

    /// <summary>Raised when the user moves it. Not raised by <see cref="Value"/> being set from outside,
    /// which is the view telling the scrollbar where it already is.</summary>
    public event Action<long>? Scrolled;

    public SlimScrollBar(LineGridControl grid)
    {
        _grid = grid;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Right;
        Width = LogicalToDeviceUnits(LogicalWidth);
        TabStop = false;
        AccessibleRole = AccessibleRole.ScrollBar;
        AccessibleName = "Vertical";
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Width = LogicalToDeviceUnits(LogicalWidth);
    }

    /// <summary>Total rows and how many of them are on screen.</summary>
    public void Configure(long rows, long visible)
    {
        _rows = Math.Max(0, rows);
        _visible = Math.Max(1, visible);
        _value = Math.Clamp(_value, 0, MaxValue);
        Invalidate();
    }

    public long MaxValue => Math.Max(0, _rows - _visible);

    [System.ComponentModel.DefaultValue(0L)]
    public long Value
    {
        get => _value;
        set
        {
            long v = Math.Clamp(value, 0, MaxValue);
            if (v == _value) return;
            _value = v;
            Invalidate();
        }
    }

    internal bool CanScroll => _rows > _visible;

    // ---- painting ----

    private int Divider => Math.Max(1, LogicalToDeviceUnits(1));
    private int TrackHeight => Math.Max(1, ClientSize.Height);

    private int ThumbHeight
    {
        get
        {
            if (_rows <= 0) return TrackHeight;
            double share = Math.Clamp((double)_visible / _rows, 0, 1);
            return (int)Math.Clamp(Math.Round(TrackHeight * share), MinThumbHeight, TrackHeight);
        }
    }

    private int ThumbTop
    {
        get
        {
            long max = MaxValue;
            if (max <= 0) return 0;
            return (int)Math.Round((double)_value / max * (TrackHeight - ThumbHeight));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var settings = _grid.Settings;
        int left = Divider;

        // A trough a shade off the gutter, and a rule down the left: side by side with the map, two strips of
        // the same colour read as one strip and neither can be aimed at.
        g.Clear(MiniMapControl.Blend(settings.Foreground, settings.GutterBack, 0.07));
        using (var rule = new SolidBrush(MiniMapControl.Blend(settings.Foreground, settings.GutterBack, 0.30)))
            g.FillRectangle(rule, 0, 0, left, ClientSize.Height);

        DrawMarks(g, left);

        int inset = Math.Max(1, LogicalToDeviceUnits(2));
        var thumb = new Rectangle(left + inset, ThumbTop, Math.Max(2, ClientSize.Width - left - inset * 2), ThumbHeight);
        double strength = !CanScroll ? 0.20 : _dragging ? 0.62 : _hot ? 0.50 : 0.38;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(thumb, Math.Max(2, thumb.Width / 2));
        using (var fill = new SolidBrush(MiniMapControl.Blend(settings.Foreground, settings.GutterBack, strength)))
            g.FillPath(fill, path);
        g.SmoothingMode = SmoothingMode.Default;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Every marked line in the file, at its place on the scrollbar's own scale.</summary>
    private void DrawMarks(Graphics g, int left)
    {
        if (_grid.Document is not { } doc || _rows <= 0) return;
        var marks = doc.Markers.Snapshot();
        if (marks.Length == 0) return;

        int width = Math.Max(1, ClientSize.Width - left);
        foreach (var (line, mask) in marks)
        {
            long row = doc.FilteredMode ? doc.RowForLine(line) : line;
            if (row < 0 || row >= _rows) continue;
            int y = (int)(row * (TrackHeight - MarkHeight) / _rows);
            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            using var brush = new SolidBrush(AppSettings.MarkerColors[Math.Clamp(index, 0, AppSettings.MarkerColors.Length - 1)]);
            g.FillRectangle(brush, left, y, width, MarkHeight);
        }
    }

    // ---- interaction ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !CanScroll) return;
        int top = ThumbTop, height = ThumbHeight;
        if (e.Y >= top && e.Y < top + height)
        {
            _dragging = true;
            _grabOffset = e.Y - top;
            Capture = true;
            Invalidate();
            return;
        }
        // Trough: a page towards the pointer, as a scrollbar has always done. The minimap next door is
        // there for landing somewhere exactly.
        Move(_value + (e.Y < top ? -_visible : _visible));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        int span = Math.Max(1, TrackHeight - ThumbHeight);
        Move((long)Math.Round((e.Y - _grabOffset) * (double)MaxValue / span));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _grid.ScrollByWheel(e.Delta);
    }

    private void Move(long to)
    {
        long v = Math.Clamp(to, 0, MaxValue);
        if (v == _value) return;
        _value = v;
        Invalidate();
        Scrolled?.Invoke(v);
        // A held drag never lets the message queue empty, and WM_PAINT only arrives when it does - so
        // without these nothing moves until the mouse stops.
        Update();
        _grid.Update();
    }

    /// <summary>Test seam: what a drag to this y would scroll to.</summary>
    internal long ValueAtForTesting(int y)
    {
        int span = Math.Max(1, TrackHeight - ThumbHeight);
        return Math.Clamp((long)Math.Round(y * (double)MaxValue / span), 0, MaxValue);
    }

    internal (int Top, int Height) ThumbForTesting => (ThumbTop, ThumbHeight);
    internal Rectangle TroughForTesting => new(Divider, 0, Math.Max(1, ClientSize.Width - Divider), ClientSize.Height);

    protected override AccessibleObject CreateAccessibilityInstance() => new BarAccessibleObject(this);

    /// <summary>Assistive technology and the UI tests both scroll a view by setting a scrollbar's value, so
    /// a drawn scrollbar has to answer for one exactly as the system control did.</summary>
    private sealed class BarAccessibleObject : Control.ControlAccessibleObject
    {
        private readonly SlimScrollBar _bar;

        public BarAccessibleObject(SlimScrollBar bar) : base(bar) => _bar = bar;

        public override AccessibleRole Role => AccessibleRole.ScrollBar;

        public override string? Value
        {
            get => _bar.Value.ToString();
            set { if (long.TryParse(value, out long v)) _bar.Move(v); }
        }
    }
}

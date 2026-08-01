using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Document;

namespace Cascade.App;

/// <summary>
/// The vertical scrollbar, drawn rather than borrowed: a third the width of the system one, and with the
/// marked lines of the whole file ticked down its trough.
///
/// The trough is the only place those marks can go. The minimap beside it shows a window of a few hundred
/// rows, so a mark outside that window has nowhere to appear - and "where are my marks in this file" is
/// exactly the question the scrollbar's own scale answers.
/// </summary>
internal sealed class SlimScrollBar : Control
{
    public const int LogicalWidth = 9;

    private const int MarkHeight = 2;      // device px; a single marked line has to be findable at file scale
    private const int MinThumbHeight = 12;

    private readonly LineGridControl _grid;
    private long _value;
    private long _rows;
    private long _visible = 1;
    private bool _dragging;
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
        long clamped = Math.Clamp(_value, 0, MaxValue);
        if (clamped != _value) { _value = clamped; }
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
            double at = (double)_value / max;
            return (int)Math.Round(at * (TrackHeight - ThumbHeight));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var settings = _grid.Settings;
        g.Clear(settings.GutterBack);

        DrawMarks(g);

        var thumb = new Rectangle(1, ThumbTop, Math.Max(1, ClientSize.Width - 2), ThumbHeight);
        using (var fill = new SolidBrush(Blend(settings.SelectionBack, settings.GutterBack, CanScroll ? 0.45 : 0.2)))
            g.FillRectangle(fill, thumb);
        using (var pen = new Pen(Blend(settings.SelectionBack, settings.GutterBack, CanScroll ? 0.8 : 0.3)))
            g.DrawRectangle(pen, thumb.X, thumb.Y, thumb.Width - 1, thumb.Height - 1);
    }

    /// <summary>Every marked line in the file, at its place on the scrollbar's own scale.</summary>
    private void DrawMarks(Graphics g)
    {
        if (_grid.Document is not { } doc || _rows <= 0) return;
        var marks = doc.Markers.Snapshot();
        if (marks.Length == 0) return;

        int width = Math.Max(1, ClientSize.Width);
        foreach (var (line, mask) in marks)
        {
            long row = doc.FilteredMode ? doc.RowForLine(line) : line;
            if (row < 0 || row >= _rows) continue;
            int y = (int)(row * (TrackHeight - MarkHeight) / _rows);
            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            using var brush = new SolidBrush(AppSettings.MarkerColors[Math.Clamp(index, 0, AppSettings.MarkerColors.Length - 1)]);
            g.FillRectangle(brush, 0, y, width, MarkHeight);
        }
    }

    private static Color Blend(Color c, Color back, double t) => Color.FromArgb(
        (int)Math.Round(back.R + (c.R - back.R) * t),
        (int)Math.Round(back.G + (c.G - back.G) * t),
        (int)Math.Round(back.B + (c.B - back.B) * t));

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
    }

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
    }

    /// <summary>Test seam: what a drag to this y would scroll to.</summary>
    internal long ValueAtForTesting(int y)
    {
        int span = Math.Max(1, TrackHeight - ThumbHeight);
        return Math.Clamp((long)Math.Round(y * (double)MaxValue / span), 0, MaxValue);
    }

    internal (int Top, int Height) ThumbForTesting => (ThumbTop, ThumbHeight);

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

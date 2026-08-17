using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>
/// A scrollbar drawn rather than borrowed, in both directions. It sits in a trough with a rule all the way
/// round it so it reads as its own strip - beside the minimap, two bare bands of much the same colour looked
/// like one band and neither could be aimed at - and its thumb is a plain rectangle in grey against the
/// map's blue window, so shape and colour both say which is which.
///
/// The vertical one also carries the marked lines of the whole file down its trough. That is the only place
/// they can go: the minimap shows a window of a few hundred rows, so a mark outside that window has nowhere
/// to appear, and "where are my marks in this file" is exactly the question the scrollbar's scale answers.
/// </summary>
internal sealed class SlimScrollBar : Control
{
    public const int LogicalWidth = 14;

    private const int MarkThickness = 2;   // device px; a single marked line has to be findable at file scale
    private const int MinThumbLength = 16;

    private readonly LineGridControl _grid;
    private readonly bool _vertical;
    private long _value;
    private long _total;
    private long _visible = 1;
    private bool _dragging;
    private bool _hot;
    private int _grabOffset;

    /// <summary>Raised when the user moves it. Not raised by <see cref="Value"/> being set from outside,
    /// which is the view telling the scrollbar where it already is.</summary>
    public event Action<long>? Scrolled;

    public SlimScrollBar(LineGridControl grid, bool vertical = true)
    {
        _grid = grid;
        _vertical = vertical;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = vertical ? DockStyle.Right : DockStyle.Bottom;
        if (vertical) Width = LogicalToDeviceUnits(LogicalWidth);
        else Height = LogicalToDeviceUnits(LogicalWidth);
        TabStop = false;
        AccessibleRole = AccessibleRole.ScrollBar;
        AccessibleName = vertical ? "Vertical" : "Horizontal";
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_vertical) Width = LogicalToDeviceUnits(LogicalWidth);
        else Height = LogicalToDeviceUnits(LogicalWidth);
    }

    /// <summary>How much there is to scroll through, and how much of it is on screen. Rows for the vertical
    /// one, pixels for the horizontal - the bar does not care which.</summary>
    public void Configure(long total, long visible)
    {
        _total = Math.Max(0, total);
        _visible = Math.Max(1, visible);
        _value = Math.Clamp(_value, 0, MaxValue);
        Invalidate();
    }

    public long MaxValue => Math.Max(0, _total - _visible);

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

    internal bool CanScroll => _total > _visible;

    // ---- geometry ----

    private int Rule => Math.Max(1, LogicalToDeviceUnits(1));

    /// <summary>The trough, inside the rule that frames it.</summary>
    private Rectangle Track
    {
        get
        {
            int r = Rule;
            return new Rectangle(r, r, Math.Max(1, ClientSize.Width - r * 2), Math.Max(1, ClientSize.Height - r * 2));
        }
    }

    private int TrackLength => Math.Max(1, _vertical ? Track.Height : Track.Width);

    private int ThumbLength
    {
        get
        {
            if (_total <= 0) return TrackLength;
            double share = Math.Clamp((double)_visible / _total, 0, 1);
            return (int)Math.Clamp(Math.Round(TrackLength * share), MinThumbLength, TrackLength);
        }
    }

    private int ThumbStart
    {
        get
        {
            long max = MaxValue;
            if (max <= 0) return 0;
            return (int)Math.Round((double)_value / max * (TrackLength - ThumbLength));
        }
    }

    private Rectangle Thumb
    {
        get
        {
            var track = Track;
            int inset = Math.Max(1, LogicalToDeviceUnits(2));
            return _vertical
                ? new Rectangle(track.Left + inset, track.Top + ThumbStart,
                                Math.Max(2, track.Width - inset * 2), ThumbLength)
                : new Rectangle(track.Left + ThumbStart, track.Top + inset,
                                ThumbLength, Math.Max(2, track.Height - inset * 2));
        }
    }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var settings = _grid.Settings;

        // A rule all the way round, not just down the side facing the text: an open-ended strip runs into
        // whatever it meets at the top and bottom, which is exactly where it meets the other scrollbar.
        using (var rule = new SolidBrush(MiniMapControl.Blend(settings.Foreground, settings.GutterBack, 0.30)))
            g.FillRectangle(rule, ClientRectangle);
        using (var trough = new SolidBrush(MiniMapControl.Blend(settings.Foreground, settings.GutterBack, 0.07)))
            g.FillRectangle(trough, Track);

        if (_vertical) DrawMarks(g);

        double strength = !CanScroll ? 0.20 : _dragging ? 0.62 : _hot ? 0.50 : 0.38;
        using var fill = new SolidBrush(MiniMapControl.Blend(settings.Foreground, settings.GutterBack, strength));
        g.FillRectangle(fill, Thumb);
    }

    /// <summary>Every marked line in the file, at its place on the scrollbar's own scale.</summary>
    private void DrawMarks(Graphics g)
    {
        if (_grid.Document is not { } doc || _total <= 0) return;
        var marks = doc.Markers.Snapshot();
        if (marks.Count == 0) return;

        var track = Track;
        foreach (var (line, mask) in marks)
        {
            long row = doc.FilteredMode ? doc.RowForLine(line) : line;
            if (row < 0 || row >= _total) continue;
            int y = track.Top + (int)(row * (track.Height - MarkThickness) / _total);
            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            g.FillRectangle(MiniMapControl.MarkerBrush(index), track.Left, y, track.Width, MarkThickness);
        }
    }

    // ---- interaction ----

    private int Along(MouseEventArgs e) => _vertical ? e.Y : e.X;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !CanScroll) return;
        var thumb = Thumb;
        int at = Along(e);
        int start = _vertical ? thumb.Top : thumb.Left;
        int length = _vertical ? thumb.Height : thumb.Width;
        if (at >= start && at < start + length)
        {
            _dragging = true;
            _grabOffset = at - start;
            Capture = true;
            Invalidate();
            return;
        }
        // Trough: a page towards the pointer, as a scrollbar has always done. The minimap next door is
        // there for landing somewhere exactly.
        ScrollTo(_value + (at < start ? -_visible : _visible));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        int span = Math.Max(1, TrackLength - ThumbLength);
        int origin = _vertical ? Track.Top : Track.Left;
        ScrollTo((long)Math.Round((Along(e) - _grabOffset - origin) * (double)MaxValue / span));
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

    /// <summary>Scrolls because the reader asked, so it reports. Setting <see cref="Value"/> does not.</summary>
    private void ScrollTo(long to)
    {
        long v = Math.Clamp(to, 0, MaxValue);
        if (v == _value) return;
        _value = v;
        Invalidate();
        Scrolled?.Invoke(v);
        // A held drag never lets the message queue empty, and WM_PAINT only arrives when it does - so
        // without this the thumb would not move until the mouse stopped. The view answers for itself: it
        // redraws no faster than the screen can show it, which this must not do - the thumb has to stay
        // under the pointer, and it costs a tenth of a millisecond to draw.
        Update();
    }

    /// <summary>Test seam: what a drag to this offset along the bar would scroll to.</summary>
    internal long ValueAtForTesting(int along)
    {
        int span = Math.Max(1, TrackLength - ThumbLength);
        int origin = _vertical ? Track.Top : Track.Left;
        return Math.Clamp((long)Math.Round((along - origin) * (double)MaxValue / span), 0, MaxValue);
    }

    internal Rectangle ThumbForTesting => Thumb;
    internal Rectangle TroughForTesting => Track;

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
            set { if (long.TryParse(value, out long v)) _bar.ScrollTo(v); }
        }
    }
}

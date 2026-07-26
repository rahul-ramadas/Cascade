using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>Column header for the filter list: <b>Filter</b> (flexible) · <b>Description</b> · <b>Count</b>,
/// with draggable dividers so the Description and Count column widths can be adjusted. Column x
/// positions are computed from the tree's content width so the header and rows stay aligned.</summary>
internal sealed class FilterListHeader : Control
{
    internal int DescriptionWidth = 160;
    internal int CountWidth = 72;
    internal Func<int>? ContentRight;
    public event Action? WidthsChanged;

    private int _dragging; // 0 none, 1 desc divider, 2 count divider

    public FilterListHeader()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = TextRenderer.MeasureText("Xg", Font).Height + 8;
        BackColor = SystemColors.Control;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = TextRenderer.MeasureText("Xg", Font).Height + 8;
    }

    private int RightEdge => Math.Max(120, ContentRight?.Invoke() ?? Width);
    private int CountX => RightEdge - CountWidth;
    private int DescX => CountX - DescriptionWidth;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SystemColors.Control);
        int rightEdge = RightEdge, countX = CountX, descX = DescX;

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        var fore = SystemColors.ControlText;
        TextRenderer.DrawText(g, "Filter", Font, new Rectangle(4, 0, Math.Max(0, descX - 8), Height), fore, flags);
        TextRenderer.DrawText(g, "Description", Font, new Rectangle(descX + 4, 0, Math.Max(0, DescriptionWidth - 8), Height), fore, flags);
        TextRenderer.DrawText(g, "Count", Font, new Rectangle(countX + 4, 0, Math.Max(0, CountWidth - 8), Height), fore, flags | TextFormatFlags.Right);

        using var pen = new Pen(SystemColors.ControlDark);
        g.DrawLine(pen, descX, 3, descX, Height - 3);
        g.DrawLine(pen, countX, 3, countX, Height - 3);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Near(e.X, DescX)) _dragging = 1;
        else if (Near(e.X, CountX)) _dragging = 2;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging == 1)
        {
            DescriptionWidth = Math.Clamp(CountX - e.X, 40, RightEdge - CountWidth - 60);
            Invalidate(); Update(); WidthsChanged?.Invoke();
        }
        else if (_dragging == 2)
        {
            CountWidth = Math.Clamp(RightEdge - e.X, 36, RightEdge - 100);
            Invalidate(); Update(); WidthsChanged?.Invoke();
        }
        else
        {
            Cursor = Near(e.X, DescX) || Near(e.X, CountX) ? Cursors.VSplit : Cursors.Default;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = 0; base.OnMouseUp(e); }

    private static bool Near(int x, int target) => Math.Abs(x - target) <= 4;
}

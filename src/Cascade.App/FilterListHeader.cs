using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>Where the filter list's columns sit, in tree client coordinates. A zero width means the column
/// is not shown at all: there is nothing to put in it.</summary>
internal readonly record struct FilterColumns(int DescX, int CountX, int DescriptionWidth, int CountWidth)
{
    public bool HasDescription => DescriptionWidth > 0;
    public bool HasCount => CountWidth > 0;

    /// <summary>Where the filter pattern has to stop - the start of whichever column comes next.</summary>
    public int FilterRight => HasDescription ? DescX : CountX;
}

/// <summary>Column header for the filter list: <b>Filter</b> · <b>Description</b> · <b>Count</b>. The widths
/// are measured from the rows' own content by <see cref="FilterTreeControl"/> and pushed in here, so the
/// header cannot disagree with what is drawn underneath it.</summary>
internal sealed class FilterListHeader : Control
{
    private FilterColumns _columns;

    public FilterListHeader()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = TextRenderer.MeasureText("Xg", Font).Height + 8;
        BackColor = SystemColors.Control;
    }

    internal int Inset => LogicalToDeviceUnits(4);

    internal void SetColumns(FilterColumns columns)
    {
        if (columns == _columns) return;
        _columns = columns;
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = TextRenderer.MeasureText("Xg", Font).Height + 8;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SystemColors.Control);

        int inset = Inset;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        var fore = SystemColors.ControlText;

        TextRenderer.DrawText(g, "Filter", Font,
            new Rectangle(inset, 0, Math.Max(0, _columns.FilterRight - inset * 2), Height), fore, flags);
        if (_columns.HasDescription)
            TextRenderer.DrawText(g, "Description", Font,
                new Rectangle(_columns.DescX + inset, 0, Math.Max(0, _columns.DescriptionWidth - inset * 2), Height), fore, flags);
        if (_columns.HasCount)
            TextRenderer.DrawText(g, "Count", Font,
                new Rectangle(_columns.CountX + inset, 0, Math.Max(0, _columns.CountWidth - inset * 2), Height), fore,
                flags | TextFormatFlags.Right);

        using var pen = new Pen(SystemColors.ControlDark);
        if (_columns.HasDescription) g.DrawLine(pen, _columns.DescX, 3, _columns.DescX, Height - 3);
        if (_columns.HasCount) g.DrawLine(pen, _columns.CountX, 3, _columns.CountX, Height - 3);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

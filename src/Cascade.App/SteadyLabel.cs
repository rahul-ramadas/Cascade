using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>A label that redraws in one go, for a message that changes under the user's fingers - a match
/// count that walks with every Enter, or a complaint about a pattern that is still being typed.
///
/// A plain label clears itself before drawing, and worse, changing its text repaints everything around it,
/// so the whole row or dialog behind it flashes on every keystroke.</summary>
internal sealed class SteadyLabel : Label
{
    private string _message = "";

    public SteadyLabel() => DoubleBuffered = true;

    /// <summary>The text to show. Deliberately not <see cref="Control.Text"/>: assigning that sends
    /// WM_SETTEXT, which changes the label's preferred size and so relayouts and repaints the whole panel
    /// behind it - measured as the entire client area of both the find bar and the filter dialog, on every
    /// keystroke. Invalidating this label alone costs its container nothing.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Message
    {
        get => _message;
        set
        {
            if (_message == value) return;
            _message = value;
            AccessibleName = value;   // so it still reads out, Text being left empty
            Invalidate();
        }
    }

    internal int Paints;

    protected override void OnPaint(PaintEventArgs e)
    {
        Paints++;
        base.OnPaint(e);
        TextRenderer.DrawText(e.Graphics, _message, Font, ClientRectangle, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool RedrawsInOneGo => GetStyle(ControlStyles.OptimizedDoubleBuffer);
}

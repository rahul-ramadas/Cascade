using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>A <see cref="TreeView"/> with real double buffering (managed + native
/// <c>TVS_EX_DOUBLEBUFFER</c>) to eliminate owner-draw flicker, and a DPI-correct row height so
/// node text is never vertically cropped.</summary>
internal sealed class BufferedTreeView : TreeView
{
    private const int TVM_SETEXTENDEDSTYLE = 0x1100 + 44;
    private const int TVS_EX_DOUBLEBUFFER = 0x0004;
    private const int TVS_NOHSCROLL = 0x8000;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public BufferedTreeView()
    {
        DoubleBuffered = true;
        ItemHeight = ComputeItemHeight();
    }

    /// <summary>No horizontal scrolling. The rows are drawn as columns under a fixed header, and the tree
    /// scrolls by shifting the pixels it already has - which slides the row text out from under the header
    /// it belongs to and smears whatever did not move. Widening the pane is the way to see more.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= TVS_NOHSCROLL;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendMessage(Handle, TVM_SETEXTENDEDSTYLE, (IntPtr)TVS_EX_DOUBLEBUFFER, (IntPtr)TVS_EX_DOUBLEBUFFER);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        int h = ComputeItemHeight();
        if (h == ItemHeight) return;
        // Setting ItemHeight recreates the handle; defer if one already exists so we never recreate
        // re-entrantly during the control-creation cascade (which throws Win32 1400).
        if (IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) ItemHeight = h; }));
        else ItemHeight = h;
    }

    private int ComputeItemHeight() => TextRenderer.MeasureText("Xygj[](", Font).Height + 8;
}

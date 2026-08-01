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
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_PAINT = 0x000F;

    /// <summary>How many times the list has actually repainted. Flicker is repaints nobody asked for, and
    /// counting them is the only way to see it without filming the screen.</summary>
    internal int Paints { get; private set; }

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

    /// <summary>The tree eats the second click of a double-click on a checkbox: the box flips but no state
    /// change is reported, so the tick and what it stands for stop agreeing. Turning it back into an ordinary
    /// click makes two quick clicks simply tick twice - which is what they look like - and stops the tree
    /// reporting a double-click on the box at all.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_PAINT) Paints++;
        if (m.Msg == WM_LBUTTONDBLCLK && IsOnCheckBox(m.LParam)) m.Msg = WM_LBUTTONDOWN;
        base.WndProc(ref m);
    }

    private bool IsOnCheckBox(IntPtr lParam)
    {
        long packed = lParam.ToInt64();
        var point = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        return HitTest(point).Location == TreeViewHitTestLocations.StateImage;
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

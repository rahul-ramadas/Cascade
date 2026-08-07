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
    private const int WM_CONTEXTMENU = 0x007B;
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

    /// <summary>Expanding a row does not move the list.
    ///
    /// Left to itself the native tree scrolls on every expansion, to fit as much of the newly revealed
    /// subtree on screen as it can - which yanks the row the user was looking at somewhere else, and can
    /// fire in the middle of a drag when a drop nests into a folded filter. Recording the first visible
    /// row before the expansion and putting it back afterwards costs nothing visible: the restore happens
    /// inside the same message, so no repaint is delivered in between.</summary>
    private TreeNode? _topBeforeExpand;

    protected override void OnBeforeExpand(TreeViewCancelEventArgs e)
    {
        base.OnBeforeExpand(e);
        _topBeforeExpand = e.Cancel ? null : TopNode;
    }

    protected override void OnAfterExpand(TreeViewEventArgs e)
    {
        var top = _topBeforeExpand;
        _topBeforeExpand = null;
        base.OnAfterExpand(e);
        if (top is not null && ReferenceEquals(top.TreeView, this) && !ReferenceEquals(top, TopNode)) TopNode = top;
    }

    /// <summary>True while a double-click on a row's own content is being handled. The tree's default answer
    /// to that is to expand or collapse the row, which is not what double-clicking a filter means - see the
    /// cancel in FilterTreeControl. The expander keeps its job, so it is excluded here.</summary>
    internal bool InContentDoubleClick { get; private set; }

    /// <summary>Whether the context menu now opening was asked for from the keyboard.</summary>
    internal bool ContextMenuFromKeyboard { get; private set; }

    /// <summary>The tree eats the second click of a double-click on a checkbox: the box flips but no state
    /// change is reported, so the tick and what it stands for stop agreeing. Turning it back into an ordinary
    /// click makes two quick clicks simply tick twice - which is what they look like - and stops the tree
    /// reporting a double-click on the box at all.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_PAINT) Paints++;
        // A menu asked for from the keyboard reports no position (lParam -1), so it belongs to the selected
        // row rather than to whatever the pointer happens to be over.
        if (m.Msg == WM_CONTEXTMENU) ContextMenuFromKeyboard = m.LParam.ToInt64() == -1;
        if (m.Msg == WM_LBUTTONDBLCLK && HitAt(m.LParam) == TreeViewHitTestLocations.StateImage) m.Msg = WM_LBUTTONDOWN;
        if (m.Msg != WM_LBUTTONDBLCLK) { base.WndProc(ref m); return; }

        InContentDoubleClick = HitAt(m.LParam) != TreeViewHitTestLocations.PlusMinus;
        try { base.WndProc(ref m); }
        finally { InContentDoubleClick = false; }

        // TreeView captures the mouse here so it is sure of getting the button-up even if that lands off
        // the control - but it raises MouseDown FIRST, and ours opens the filter editor. The up is then
        // delivered while this window is disabled by the modal dialog, so it never arrives, and the tree
        // holds the capture indefinitely: the user's next click goes to the list wherever it was aimed,
        // and is swallowed. There is nothing left to wait for once the button is up.
        if ((MouseButtons & MouseButtons.Left) == 0) Capture = false;
    }

    private TreeViewHitTestLocations HitAt(IntPtr lParam)
    {
        long packed = lParam.ToInt64();
        return HitTest(new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF))).Location;
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

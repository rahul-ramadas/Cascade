using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>A <see cref="TreeView"/> with real double buffering (managed + native
/// <c>TVS_EX_DOUBLEBUFFER</c>) to eliminate owner-draw flicker, and a DPI-correct row height so
/// node text is never vertically cropped.</summary>
internal sealed class BufferedTreeView : TreeView
{
    private const int TVM_SETEXTENDEDSTYLE = 0x1100 + 44;
    private const int TVM_SETBORDER = 0x1100 + 35;
    private const int TVSBF_XBORDER = 0x0001;
    private const int TVS_EX_DOUBLEBUFFER = 0x0004;
    private const int TVS_NOHSCROLL = 0x8000;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_PAINT = 0x000F;
    private const int WM_REFLECT_NOTIFY = 0x204E;
    private const int NM_CUSTOMDRAW = -12;
    private const int CDDS_PREPAINT = 1;
    private const int CDDS_POSTPAINT = 2;
    private const int CDRF_NOTIFYPOSTPAINT = 0x10;

    private int _leftBorder;
    private bool _borderPressed;
    internal event Action<Graphics>? EmptyAreaPaint;

    [StructLayout(LayoutKind.Sequential)]
    private struct NotificationHeader
    {
        public IntPtr Window;
        public IntPtr Id;
        public int Code;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CustomDraw
    {
        public NotificationHeader Header;
        public int Stage;
        public IntPtr DeviceContext;
    }

    /// <summary>How many times the list has actually repainted. Flicker is repaints nobody asked for, and
    /// counting them is the only way to see it without filming the screen.</summary>
    internal int Paints { get; private set; }

    /// <summary>Insets every row from the left edge of the client area, leaving a strip at a FIXED x that
    /// the owner draw can use as a gutter. Everything else in a row - the tree lines, the expander, the
    /// checkbox - moves with the row's depth, so this is the only place a real column can be put.</summary>
    internal void SetLeftBorder(int px)
    {
        if (px == _leftBorder) return;
        _leftBorder = px;
        if (IsHandleCreated) ApplyLeftBorder();
    }

    /// <summary>Where the rows start, which is where the gutter ends.</summary>
    internal int LeftBorder => _leftBorder;

    private void ApplyLeftBorder()
    {
        SendMessage(Handle, TVM_SETBORDER, (IntPtr)TVSBF_XBORDER, (IntPtr)(_leftBorder & 0xFFFF));
        Invalidate();
    }

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
        ApplyLeftBorder();   // a new handle knows nothing of the border the old one was given
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
        if (m.Msg is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK)
        {
            var point = PointAt(m.LParam);
            if (point.X >= 0 && point.X < _leftBorder && GetNodeAt(0, point.Y) is not null)
            {
                Focus();
                _borderPressed = true;
                Capture = true;
                OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                return;
            }
        }
        if (m.Msg == WM_LBUTTONUP && _borderPressed)
        {
            var point = PointAt(m.LParam);
            _borderPressed = false;
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
            Capture = false;
            return;
        }
        if (m.Msg == WM_REFLECT_NOTIFY && Marshal.ReadInt32(m.LParam, IntPtr.Size * 2) == NM_CUSTOMDRAW)
        {
            var draw = Marshal.PtrToStructure<CustomDraw>(m.LParam);
            base.WndProc(ref m);
            if (draw.Stage == CDDS_PREPAINT) m.Result |= CDRF_NOTIFYPOSTPAINT;
            else if (draw.Stage == CDDS_POSTPAINT && Nodes.Count == 0 && EmptyAreaPaint is { } paint)
            {
                using var graphics = Graphics.FromHdc(draw.DeviceContext);
                paint(graphics);
            }
            return;
        }
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

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) _borderPressed = false;
        base.OnMouseCaptureChanged(e);
    }

    private static Point PointAt(IntPtr lParam)
    {
        long packed = lParam.ToInt64();
        return new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }

    private TreeViewHitTestLocations HitAt(IntPtr lParam) => HitTest(PointAt(lParam)).Location;

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

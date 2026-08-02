using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Find;

namespace Cascade.App;

/// <summary>
/// The find bar: one row above the log, holding the term, the two options, the count of what it matched,
/// and a way out.
///
/// It is a panel rather than a window on purpose. A floating dialog opened over the middle of the text —
/// the very thing being searched — so it had to be dismissed to read the results, which left the search
/// running with nothing on screen to say so. Sitting in the layout it costs two rows and never has to be
/// dismissed, so a term is being looked for exactly when the bar is up.
///
/// No mnemonics anywhere on it: the bar lives in a window that owns a menu bar, and an Alt key here would
/// fight with the menu's. Ctrl+F, Enter, F3 and Esc reach it wherever the focus is - see MainForm.
/// </summary>
public sealed class FindBar : UserControl
{
    private readonly ComboBox _text = new() { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.None };
    private readonly CheckBox _regex = new() { Text = "Regex", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox _case = new() { Text = "Case sensitive", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly SteadyLabel _message = new() { AutoSize = false, AutoEllipsis = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _next = new() { Text = "Next" };
    private readonly Button _prev = new() { Text = "Previous" };
    private readonly Button _close = new() { Text = "\u2715", FlatStyle = FlatStyle.Flat };
    private readonly ToolTip _tip = new();
    private readonly Action<FindQuery, bool> _search;
    private readonly System.Windows.Forms.Timer _preview = new() { Interval = 200 };
    private readonly Font _mono = new("Consolas", 9.75f);
    private bool _searching;
    private TableLayoutPanel _root = null!;
    private int _rowHeight;

    protected override void Dispose(bool disposing)
    {
        // A font handed to a control is not disposed with it, and this bar is hidden rather than destroyed.
        if (disposing) { _preview.Dispose(); _mono.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Raised when the user asks to put the bar away (the close button; Esc is handled by the form).</summary>
    public event Action? CloseRequested;

    /// <summary>Raised shortly after the term changes, with null for an empty one. Only marks what is
    /// already on screen - it deliberately does not search, so typing never moves the view.</summary>
    public event Action<FindQuery?>? PreviewChanged;

    public FindBar(Action<FindQuery, bool> search)
    {
        _search = search;
        Dock = DockStyle.Top;
        Visible = false;
        BackColor = SystemColors.Control;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AccessibleName = "Find bar";

        int Dpi(int v) => LogicalToDeviceUnits(v);
        _text.Font = _mono;
        _text.AccessibleName = "Find what";
        _next.AccessibleName = "Find next";
        _prev.AccessibleName = "Find previous";
        _close.AccessibleName = "Close find";
        // The message deliberately has no AccessibleName: setting one REPLACES the name UI Automation
        // reports, and this label's text is the thing worth reading.

        // One row, one table. The row is exactly one control tall and everything is anchored to its TOP, so
        // they all start on the same line by construction. Left to centre themselves in a tall cell they do
        // not: the leftover space is halved and rounded down, and a ComboBox keeps its own height whatever
        // it is given - which put it a pixel above the rest.
        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            Padding = new Padding(Dpi(6), 0, Dpi(4), 0)
        };
        foreach (var w in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize,
                                  SizeType.AutoSize, SizeType.AutoSize })
            _root.ColumnStyles.Add(new ColumnStyle(w));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // the count takes what is left
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var findLabel = new Label
        {
            Text = "Find:",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, Dpi(6), 0)
        };
        _text.Margin = new Padding(0, 0, Dpi(8), 0);
        _text.Anchor = AnchorStyles.Top | AnchorStyles.Left;

        foreach (var b in new[] { _prev, _next })
        {
            b.AutoSize = true;
            b.MinimumSize = new Size(Dpi(64), Dpi(23));
            b.Margin = new Padding(0, 0, Dpi(4), 0);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        }
        _next.Margin = new Padding(0, 0, Dpi(10), 0);
        _regex.Margin = new Padding(0, 0, Dpi(10), 0);
        _case.Margin = new Padding(0, 0, Dpi(10), 0);

        _message.ForeColor = SystemColors.GrayText;
        _message.BackColor = SystemColors.Control;
        _message.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;   // stretches across
        _message.Height = Dpi(23);
        _message.Margin = new Padding(0, 0, Dpi(8), 0);

        _close.AutoSize = false;
        _close.Size = new Size(Dpi(22), Dpi(22));
        _close.FlatAppearance.BorderSize = 0;
        _close.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _close.Margin = new Padding(0);
        _close.TabStop = false;
        _tip.SetToolTip(_close, "Close find (Esc)");

        var options = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,     // or two checkboxes stack up one per line in an auto-sized column
            Margin = new Padding(0),
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        options.Controls.Add(_regex);
        options.Controls.Add(_case);

        _root.Controls.Add(findLabel, 0, 0);
        _root.Controls.Add(_text, 1, 0);
        _root.Controls.Add(_prev, 2, 0);
        _root.Controls.Add(_next, 3, 0);
        _root.Controls.Add(options, 4, 0);
        _root.Controls.Add(_message, 5, 0);
        _root.Controls.Add(_close, 6, 0);
        Controls.Add(_root);

        // Every control on the row is given the SAME height, and the row is made exactly that tall, so the
        // table has no leftover space to divide up and they cannot land on different lines.
        _rowHeight = _text.PreferredHeight;
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, _rowHeight));
        SameHeight(findLabel, _rowHeight);
        SameHeight(_regex, _rowHeight);
        SameHeight(_case, _rowHeight);
        _prev.MinimumSize = _next.MinimumSize = new Size(Dpi(64), _rowHeight);
        _prev.MaximumSize = _next.MaximumSize = new Size(0, _rowHeight);
        _message.Height = _rowHeight;
        _close.Size = new Size(_rowHeight, _rowHeight);

        static void SameHeight(Control c, int height)
        {
            int width = c.PreferredSize.Width;   // read while it still sizes itself
            c.AutoSize = false;
            c.Size = new Size(width, height);
        }

        _prev.Click += (_, _) => Run(false);
        _next.Click += (_, _) => Run(true);
        _close.Click += (_, _) => CloseRequested?.Invoke();

        // Typing marks the hits already on screen, after a pause so a burst of keystrokes costs one pass.
        _text.TextChanged += (_, _) => { _preview.Stop(); _preview.Start(); };
        _text.DropDown += (_, _) => { if (History is { } h) SetHistory(h()); };
        _text.KeyDown += (_, e) =>
        {
            if (e.Alt || e.Control || e.Shift) return;
            if (e.KeyCode is not (Keys.Down or Keys.Up)) return;
            // A closed drop-down would change the term with no sign of where in the list it came from.
            StepHistory(e.KeyCode == Keys.Down ? 1 : -1);
            e.Handled = e.SuppressKeyPress = true;
        };
        _regex.CheckedChanged += (_, _) => { _preview.Stop(); _preview.Start(); };
        _case.CheckedChanged += (_, _) => { _preview.Stop(); _preview.Start(); };
        _preview.Tick += (_, _) =>
        {
            _preview.Stop();
            PreviewChanged?.Invoke(_text.Text.Length == 0 ? null : Query());
        };
    }

    /// <summary>A hairline under the bar, so the log below reads as a separate surface rather than as text
    /// that happens to start lower down.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        _barPaints++;
        base.OnPaint(e);
        using var pen = new Pen(SystemColors.ControlDark);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    private int _barPaints;

    /// <summary>The bar is a fixed height rather than auto-sized around its contents. An auto-sizing
    /// container hands a percentage column only as much width as its content asks for, which left the count
    /// squeezed into a couple of characters at the end of a very wide row.</summary>
    private int NaturalHeight => _text.PreferredHeight + LogicalToDeviceUnits(12);

    /// <summary>Rounds the bar up to a whole number of log lines. Opening it then takes an exact number of
    /// lines off the view, so the divider below it never has to move to keep the remaining ones whole - and
    /// the filter pane keeps the size the user gave it.</summary>
    internal void SnapHeightTo(int rowPitch)
    {
        _rowPitch = rowPitch;
        ApplyHeight();
    }

    private int _rowPitch;

    private void ApplyHeight()
    {
        Height = _rowPitch <= 0 ? NaturalHeight : (NaturalHeight + _rowPitch - 1) / _rowPitch * _rowPitch;
        CentreRow();
    }

    /// <summary>Puts the row of controls in the middle of the bar. The bar is stretched to a whole number of
    /// log lines, so whatever is left over goes above and below the row rather than inside the table, where
    /// each cell would divide it up for itself.</summary>
    private void CentreRow()
    {
        int lead = Math.Max(0, (Height - _rowHeight) / 2);
        var p = _root.Padding;
        if (p.Top == lead) return;
        _root.Padding = new Padding(p.Left, lead, p.Right, Math.Max(0, Height - _rowHeight - lead));
    }

    /// <summary>The term box takes whatever is left once the rest of the row and a readable count have had
    /// their share, between a width that always fits a useful term and one past which a search box just
    /// looks odd. The count's share is a fixed measurement of a representative tally, not of the one it is
    /// showing, so the row cannot shuffle about as the numbers change.</summary>
    private void SizeTermBox()
    {
        if (_root.Controls.Count == 0) return;
        int others = _root.Padding.Horizontal;
        foreach (Control c in _root.Controls)
            if (!ReferenceEquals(c, _text) && !ReferenceEquals(c, _message))
                others += c.Width + c.Margin.Horizontal;

        int spare = Math.Max(0, Width - others - _text.Margin.Horizontal - _message.Margin.Horizontal);
        int want = Math.Clamp(spare - CountWidth, LogicalToDeviceUnits(220), LogicalToDeviceUnits(620));
        if (_text.Width != want) _text.Width = want;
    }

    /// <summary>Room set aside for the count: a tally long enough to be worth reading whole.</summary>
    private int CountWidth =>
        TextRenderer.MeasureText("Match 999,999 of 999,999 lines \u00b7 999,999 hidden", Font).Width;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        CentreRow();
        SizeTermBox();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ApplyHeight();
    }

    /// <summary>A label that redraws in one go. The count changes with every keypress that walks to the next
    /// match, and a plain label clears itself before drawing, which on a strip this wide reads as a flash.</summary>
    private sealed class SteadyLabel : Label
    {
        private string _message = "";

        public SteadyLabel() => DoubleBuffered = true;

        /// <summary>The text to show. Deliberately not <see cref="Control.Text"/>: assigning that sends
        /// WM_SETTEXT, which was measured to repaint the whole bar behind this label - so the term box, the
        /// options and the buttons were all being cleared and redrawn on every keypress that walked to the
        /// next match. Invalidating this label alone costs the bar nothing.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
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

        internal bool RedrawsInOneGo => GetStyle(ControlStyles.OptimizedDoubleBuffer);
    }

    /// <summary>Enter searches for what is in the box. Handled here rather than left to the form because
    /// ProcessCmdKey runs on the focused control first, so this sees the key before anything else can.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            // With the list of earlier terms open, Enter is choosing one of them.
            case Keys.Enter when !_text.DroppedDown:
                Run(true);
                return true;
            case Keys.Shift | Keys.Enter:
                Run(false);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Where the terms searched for before come from. Read when the drop-down opens rather than
    /// pushed after every search: rebuilding the list puts the caret back to the start of the box and drops
    /// any selection, which is not what pressing Enter should do to what you just typed.
    /// A field, not a property - the WinForms analyser flags public properties on a Control.</summary>
    public Func<IEnumerable<string>>? History;

    public bool HasTerm => _text.Text.Length > 0;

    /// <summary>Fills the drop-down with the terms searched for before, most recent first.</summary>
    public void SetHistory(IEnumerable<string> terms)
    {
        string current = _text.Text;
        int at = _text.SelectionStart, length = _text.SelectionLength;
        _text.Items.Clear();
        foreach (var t in terms) _text.Items.Add(t);
        if (_text.Text != current) _text.Text = current;
        _text.SelectionStart = at;
        _text.SelectionLength = length;
    }

    /// <summary>Puts a term in the box, as Ctrl+F does with whatever is picked out in the log.</summary>
    public void SetTerm(string term)
    {
        if (_text.Text == term) return;
        _text.Text = term;
    }

    private FindQuery Query() => new(_text.Text, _regex.Checked, _case.Checked);

    /// <summary>Searches for what is in the box. Does nothing while a search is already running, so holding
    /// the key down cannot stack them up.</summary>
    public void Run(bool forward)
    {
        if (_searching || _text.Text.Length == 0) return;
        _search(Query(), forward);
    }

    /// <summary>What the search found, or what went wrong. One label for both: a search that found nothing
    /// has no tally to show, and a search that found something has nothing to complain about.</summary>
    public void SetMessage(string text, string? detail = null)
    {
        _message.Message = text;
        string want = detail ?? text;
        if (_tip.GetToolTip(_message) != want) _tip.SetToolTip(_message, want);
    }

    /// <summary>Paints the count now rather than when the message queue next empties. Holding Enter down to
    /// walk the matches never lets it empty, so without this the count sits at whatever it read when the key
    /// went down and only catches up on release.</summary>
    public void PaintNow() => _message.Update();

    /// <summary>Blocks the find buttons while a background search runs.</summary>
    public void SetSearching(bool searching)
    {
        if (_searching == searching) return;
        _searching = searching;
        _next.Enabled = _prev.Enabled = !searching;
    }

    public void FocusInput()
    {
        _text.Focus();
        _text.SelectionStart = 0;
        _text.SelectionLength = _text.Text.Length;
    }

    /// <summary>Opens the list of earlier terms and moves through it. The first press picks the most recent,
    /// which is what a search box that remembers is for.</summary>
    private void StepHistory(int delta)
    {
        // Only refill it on the way in: rebuilding the list drops the place in it, so refreshing on every
        // press would leave Down stuck on the most recent term.
        if (!_text.DroppedDown)
        {
            if (History is { } h) SetHistory(h());
            if (_text.Items.Count == 0) return;
            _text.DroppedDown = true;
        }
        if (_text.Items.Count == 0) return;
        int next = Math.Clamp(_text.SelectedIndex + delta, 0, _text.Items.Count - 1);
        if (next != _text.SelectedIndex) _text.SelectedIndex = next;
    }

    internal Font FontForTesting => _mono;
    internal int TermBoxWidthForTesting => _text.Width;
    internal int MessageWidthForTesting => _message.Width;
    internal int CountWidthForTesting => CountWidth;
    internal bool MessageRedrawsInOneGoForTesting => _message.RedrawsInOneGo;
    internal int BarPaintsForTesting => _barPaints;
    internal int MessagePaintsForTesting => _message.Paints;
    internal void RepaintMessageForTesting() { _message.Invalidate(); _message.Update(); }
    internal void StepHistoryForTesting(int delta) => StepHistory(delta);
    internal bool HistoryIsOpenForTesting() => _text.DroppedDown;
    internal string TermForTesting() => _text.Text;
    internal string MessageForTesting() => _message.Message;
    internal (int Start, int Length) SelectionForTesting() => (_text.SelectionStart, _text.SelectionLength);
    internal bool SearchingForTesting() => _searching;
    internal void EnterForTesting() => Run(true);

    internal void SetTermForTesting(string text, int start, int length)
    {
        _text.Text = text;
        _text.SelectionStart = start;
        _text.SelectionLength = length;
    }
}

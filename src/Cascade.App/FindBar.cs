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
/// The only Alt keys on it are the two options, R and C, which the menu bar does not claim. Ctrl+F, Enter,
/// F3 and Esc reach it wherever the focus is - see MainForm.
/// </summary>
public sealed class FindBar : UserControl
{
    private readonly ComboBox _text = new() { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.None };
    private readonly QuietCheckBox _regex = new() { Text = "&Regex", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly QuietCheckBox _case = new() { Text = "&Case sensitive", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly SteadyLabel _message = new() { AutoSize = false, AutoEllipsis = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _next = new() { Text = "Next" };
    private readonly Button _prev = new() { Text = "Previous" };
    private readonly Button _close = new() { Text = "\u2715", FlatStyle = FlatStyle.Flat };
    private readonly Panel _rule = new();
    private readonly ToolTip _tip = new();
    private readonly Action<FindQuery, bool> _search;
    private bool _searching;
    private TableLayoutPanel _root = null!;
    private int _rowHeight;
    private string _tally = "", _tallyDetail = "", _regexError = "";

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tip.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Raised when the user asks to put the bar away (the close button; Esc is handled by the form).</summary>
    public event Action? CloseRequested;

    /// <summary>Raised as the term changes, with null for an empty one. Only marks what is already on
    /// screen - it deliberately does not search, so typing never moves the view. That is also why it is not
    /// held back for a moment first: marking costs one pass over the handful of lines being displayed.</summary>
    public event Action<FindQuery?>? PreviewChanged;

    public FindBar(Action<FindQuery, bool> search)
    {
        _search = search;
        Dock = DockStyle.Top;
        Visible = false;
        BackColor = SystemColors.Control;
        Padding = new Padding(0, 0, 0, 1);   // the row must not cover the hairline along the bottom
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AccessibleName = "Find bar";

        int Dpi(int v) => LogicalToDeviceUnits(v);
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
            ColumnCount = 8,
            RowCount = 1,
            Padding = new Padding(Dpi(6), 0, Dpi(4), 0)
        };
        foreach (var w in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize,
                                  SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize })
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
            // Sized rather than auto-sized: an auto-sizing button draws its caption a pixel lower still.
            // A pixel below the labels is as close as it gets - a themed button pins its caption to a fixed
            // offset from its top, and MEASURING showed neither height nor Padding moves it (padding just
            // clips the text). The caption-ink check in the self-test records where they all land.
            b.AutoSize = false;
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

        // A rule where the count begins: the row then reads as what is being looked for on one side and
        // what was found on the other. A control rather than something painted on, so the table keeps it in
        // step with the box beside it as that grows.
        _rule.BackColor = Blend(SystemColors.ControlDark, SystemColors.Control, 0.45);
        _rule.Margin = new Padding(0, 0, Dpi(9), 0);
        _rule.Anchor = AnchorStyles.Top | AnchorStyles.Left;

        _root.Controls.Add(findLabel, 0, 0);
        _root.Controls.Add(_text, 1, 0);
        _root.Controls.Add(_prev, 2, 0);
        _root.Controls.Add(_next, 3, 0);
        _root.Controls.Add(options, 4, 0);
        _root.Controls.Add(_rule, 5, 0);
        _root.Controls.Add(_message, 6, 0);
        _root.Controls.Add(_close, 7, 0);
        Controls.Add(_root);

        // Every control on the row is given the SAME height and sized explicitly, and the row is made
        // exactly that tall. The table then has no leftover space to divide up, so nothing can land on a
        // different line - and an AUTO-SIZED control must not be left on the row at all: a button that sizes
        // itself draws its caption a pixel below one of the same height that was given its size.
        _rowHeight = _text.PreferredHeight;
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, _rowHeight));
        SameHeight(findLabel, _rowHeight);
        SameHeight(_regex, _rowHeight);
        SameHeight(_case, _rowHeight);
        SameHeight(_prev, _rowHeight, Dpi(64));
        SameHeight(_next, _rowHeight, Dpi(64));
        _message.Height = _rowHeight;
        _close.Size = new Size(_rowHeight, _rowHeight);
        _rule.Size = new Size(1, _rowHeight);

        static void SameHeight(Control c, int height, int least = 0)
        {
            int width = Math.Max(least, c.PreferredSize.Width);   // read while it still sizes itself
            c.AutoSize = false;
            c.Size = new Size(width, height);
        }

        _prev.Click += (_, _) => Run(false);
        _next.Click += (_, _) => Run(true);
        _close.Click += (_, _) => CloseRequested?.Invoke();

        // Typing marks the hits already on screen, as it is typed: only the rows being displayed are looked
        // at, so there is nothing to save by waiting for a pause in the keystrokes.
        _text.TextChanged += (_, _) => { ValidateRegex(); Preview(); };
        _text.DropDown += (_, _) => { if (History is { } h) SetHistory(h()); };
        _text.KeyDown += (_, e) =>
        {
            if (e.Alt || e.Control || e.Shift) return;
            if (e.KeyCode is not (Keys.Down or Keys.Up)) return;
            // A closed drop-down would change the term with no sign of where in the list it came from.
            StepHistory(e.KeyCode == Keys.Down ? 1 : -1);
            e.Handled = e.SuppressKeyPress = true;
        };
        _regex.CheckedChanged += (_, _) => { ValidateRegex(); Preview(); };
        _case.CheckedChanged += (_, _) => Preview();
    }

    private void Preview() => PreviewChanged?.Invoke(_text.Text.Length == 0 ? null : Query());

    /// <summary>Tab, kept inside the bar. Handing it to the form instead walks out of the bar and into
    /// whatever control happens to be next in the window, which is no use to anyone: the way out of a bar
    /// is Escape.</summary>
    internal void MoveFocusWithin(bool forward) =>
        SelectNextControl(ActiveControl, forward, tabStopOnly: true, nested: true, wrap: true);

    /// <summary>A hairline under the bar, so the log below reads as a separate surface rather than as text
    /// that happens to start lower down. The bar keeps a pixel of padding for it, or the table filling the
    /// bar would cover it.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        _barPaints++;
        base.OnPaint(e);
        using var pen = new Pen(SystemColors.ControlDark);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    /// <summary>Where the count begins, in the bar's own coordinates.</summary>
    private int CountStartsAt => _rule.Width > 0 ? _root.Left + _rule.Left : 0;

    private static Color Blend(Color a, Color b, double towardsB) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * towardsB),
        (int)(a.G + (b.G - a.G) * towardsB),
        (int)(a.B + (b.B - a.B) * towardsB));

    private int _barPaints;

    /// <summary>The bar is a fixed height rather than auto-sized around its contents. An auto-sizing
    /// container hands a percentage column only as much width as its content asks for, which left the count
    /// squeezed into a couple of characters at the end of a very wide row.
    /// The air added here is deliberately small: the height is then rounded UP to whole log lines, so
    /// padding it generously first only risks tipping the bar into an extra line it does not need.</summary>
    private int NaturalHeight => _text.PreferredHeight + LogicalToDeviceUnits(4);

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
    /// the key down cannot stack them up, and nothing while the pattern will not compile - the message
    /// already says why, and searching for it would only report that nothing was found.</summary>
    public void Run(bool forward)
    {
        if (_searching || _text.Text.Length == 0 || _regexError.Length > 0) return;
        _search(Query(), forward);
    }

    /// <summary>What the search found, or what went wrong. One label for both: a search that found nothing
    /// has no tally to show, and a search that found something has nothing to complain about.</summary>
    public void SetMessage(string text, string? detail = null)
    {
        _tally = text;
        _tallyDetail = detail ?? text;
        ShowMessage();
    }

    /// <summary>Complains about a pattern that will not compile. It goes where the count goes because the
    /// two can never both apply - a term that cannot be parsed has not matched anything to count.</summary>
    private void ValidateRegex()
    {
        string problem = "";
        if (_regex.Checked && _text.Text.Length > 0)
        {
            try { _ = System.Text.RegularExpressions.Regex.Match("", _text.Text); }
            catch (ArgumentException ex) { problem = "Invalid regex: " + ex.Message; }
        }
        if (_regexError == problem) return;
        _regexError = problem;
        ShowMessage();
    }

    private void ShowMessage()
    {
        bool bad = _regexError.Length > 0;
        _message.ForeColor = bad ? Color.Firebrick : SystemColors.GrayText;
        _message.Message = bad ? _regexError : _tally;
        string want = bad ? _regexError : _tallyDetail;
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

    internal int TermBoxWidthForTesting => _text.Width;
    internal int RowHeightForTesting => _rowHeight;
    internal int MessageWidthForTesting => _message.Width;
    internal int CountWidthForTesting => CountWidth;
    internal int CountStartsAtForTesting => CountStartsAt;
    internal Color MessageColourForTesting => _message.ForeColor;
    internal bool RegexIsOnForTesting => _regex.Checked;
    internal bool CaseIsOnForTesting => _case.Checked;
    internal bool TermBoxHasFocusForTesting => _text.Focused;
    internal string FocusedForTesting =>
        _text.Focused ? "term" : _regex.Focused ? "regex" : _case.Focused ? "case"
        : _prev.Focused ? "previous" : _next.Focused ? "next" : _close.Focused ? "close" : "none";
    internal void SetRegexForTesting(bool on) => _regex.Checked = on;
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

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
    private readonly SteadyLabel _message = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _next = new() { Text = "Next" };
    private readonly Button _prev = new() { Text = "Previous" };
    private readonly Button _close = new() { Text = "\u2715", FlatStyle = FlatStyle.Flat };
    private readonly ToolTip _tip = new();
    private readonly Action<FindQuery, bool> _search;
    private readonly System.Windows.Forms.Timer _preview = new() { Interval = 200 };
    private readonly Font _mono = new("Consolas", 9.75f);
    private bool _searching;

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

        // The left-hand group sizes to its contents, the close button takes the right edge, and the message
        // fills whatever is between them. Docking rather than a percentage column because an auto-sizing
        // table hands a percentage only what its content asks for, which left the count two characters wide.
        // Order matters: the last control added is docked first, so the filling one goes in first.
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(Dpi(6), 0, 0, 0)
        };
        for (int i = 0; i < left.ColumnCount; i++) left.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var findLabel = new Label
        {
            Text = "Find:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,     // left only, so the cell centres it vertically
            Margin = new Padding(0, 0, Dpi(6), 0)
        };
        _text.Margin = new Padding(0, 0, Dpi(8), 0);
        _text.Width = Dpi(220);
        _text.Anchor = AnchorStyles.Left;

        foreach (var b in new[] { _prev, _next })
        {
            b.AutoSize = true;
            b.MinimumSize = new Size(Dpi(64), Dpi(23));
            b.Margin = new Padding(0, 0, Dpi(4), 0);
            b.Anchor = AnchorStyles.Left;
        }
        _next.Margin = new Padding(0, 0, Dpi(10), 0);
        _regex.Margin = new Padding(0, 0, Dpi(10), 0);
        _case.Margin = new Padding(0, 0, Dpi(10), 0);

        _message.ForeColor = SystemColors.GrayText;
        _message.Padding = new Padding(0, 0, Dpi(8), 0);

        _close.AutoSize = false;
        _close.Size = new Size(Dpi(22), Dpi(22));
        _close.FlatAppearance.BorderSize = 0;
        _close.Dock = DockStyle.Right;
        _close.TabStop = false;
        _tip.SetToolTip(_close, "Close find (Esc)");

        var options = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,     // or two checkboxes stack up one per line in an auto-sized column
            Margin = new Padding(0),
            Anchor = AnchorStyles.Left
        };
        options.Controls.Add(_regex);
        options.Controls.Add(_case);

        left.Controls.Add(findLabel, 0, 0);
        left.Controls.Add(_text, 1, 0);
        left.Controls.Add(_prev, 2, 0);
        left.Controls.Add(_next, 3, 0);
        left.Controls.Add(options, 4, 0);

        Controls.Add(_message);
        Controls.Add(left);
        Controls.Add(_close);

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
        base.OnPaint(e);
        using var pen = new Pen(SystemColors.ControlDark);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

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
        => Height = _rowPitch <= 0 ? NaturalHeight : (NaturalHeight + _rowPitch - 1) / _rowPitch * _rowPitch;

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
        public SteadyLabel() => DoubleBuffered = true;
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
        if (_message.Text != text) _message.Text = text;
        string want = detail ?? text;
        if (_tip.GetToolTip(_message) != want) _tip.SetToolTip(_message, want);
    }

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
    internal bool MessageRedrawsInOneGoForTesting => _message.RedrawsInOneGo;
    internal void StepHistoryForTesting(int delta) => StepHistory(delta);
    internal bool HistoryIsOpenForTesting() => _text.DroppedDown;
    internal string TermForTesting() => _text.Text;
    internal string MessageForTesting() => _message.Text;
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

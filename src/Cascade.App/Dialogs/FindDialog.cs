using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Find;

namespace Cascade.App;

/// <summary>Modeless find bar. Hides (rather than disposing) on Esc or close so it can be reused,
/// and reports status (e.g. still-loading) back via <see cref="SetStatus"/>.</summary>
public sealed class FindDialog : Form
{
    private readonly ComboBox _text = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.None };
    private readonly CheckBox _regex = new() { Text = "&Regex", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox _case = new() { Text = "&Case sensitive", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Gray };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Continuous, Maximum = 1000, Visible = false };
    private readonly Button _next = new() { Text = "&Find Next" };
    private readonly Button _prev = new() { Text = "Find &Previous" };
    private readonly Button _cancel = new() { Text = "Cancel", Visible = false };
    private readonly Action<FindQuery, bool> _search;
    private readonly System.Windows.Forms.Timer _preview = new() { Interval = 200 };
    private bool _searching;

    /// <summary>Raised when the user clicks Cancel while a search is running.</summary>
    public event Action? CancelRequested;

    /// <summary>Raised shortly after the term changes, with null for an empty one. Only marks what is
    /// already on screen - it deliberately does not search, so typing never moves the view.</summary>
    public event Action<FindQuery?>? PreviewChanged;

    public FindDialog(Action<FindQuery, bool> search)
    {
        _search = search;
        Text = "Find";
        // Use a standard fixed dialog frame (not a tool window) so the close button matches the app's
        // other dialogs — the tool-window caption renders a small, inset close button that looks off.
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        int Dpi(int v) => LogicalToDeviceUnits(v);
        _text.Font = new Font("Consolas", 9.75f);
        _text.AccessibleName = "Find what";

        // Two columns: a label column that sizes itself, and a field column that takes the rest. Everything
        // then lines up down the dialog instead of each row finding its own left edge.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(Dpi(14), Dpi(12), Dpi(14), Dpi(10))
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // Every cell is given explicitly. Left to place things itself, a TableLayoutPanel deals its cells
        // out to the VISIBLE controls in order - so the moment the status label appears it takes the label
        // column's cell and shoves the whole field column sideways.
        root.RowCount = 3;
        for (int i = 0; i < root.RowCount; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var findLabel = new Label
        {
            Text = "Fi&nd:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, Dpi(7), Dpi(10), Dpi(4))
        };
        _text.Margin = new Padding(0, Dpi(4), 0, Dpi(4));
        _text.MinimumSize = new Size(Dpi(340), 0);
        root.Controls.Add(findLabel, 0, 0);
        root.Controls.Add(_text, 1, 0);

        var options = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0)
        };
        _regex.Margin = new Padding(0, Dpi(3), Dpi(24), Dpi(3));
        _case.Margin = new Padding(0, Dpi(3), 0, Dpi(3));
        options.Controls.Add(_regex);
        options.Controls.Add(_case);

        // The message shares the checkbox row instead of having one of its own. That row stands as tall as
        // the checkboxes whether or not there is anything to report, so a message costs no height - and the
        // width left over beside two checkboxes is far more than a row of its own would have given it.
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleRight;
        _status.AutoEllipsis = true;
        _status.Margin = new Padding(Dpi(16), 0, 0, 0);

        var optionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, Dpi(2), 0, Dpi(2))
        };
        optionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        optionRow.Controls.Add(options, 0, 0);
        optionRow.Controls.Add(_status, 1, 0);
        root.Controls.Add(optionRow, 1, 1);

        foreach (var b in new[] { _next, _prev, _cancel })
        {
            b.AutoSize = true;
            b.MinimumSize = new Size(Dpi(104), Dpi(26));
            b.Margin = new Padding(Dpi(6), 0, 0, 0);
        }
        _next.Click += (_, _) => Run(true);
        _prev.Click += (_, _) => Run(false);
        _cancel.Click += (_, _) => CancelRequested?.Invoke();
        // Kept on screen and merely disabled: appearing and disappearing would shuffle the buttons beside it.
        _cancel.Visible = true;
        _cancel.Enabled = false;

        // Right-aligned, like every other dialog's buttons; they used to hang off the left with a wide gap.
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,        // or they stack up one per line inside an auto-sized column
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };
        buttons.Controls.Add(_next);     // rightmost, and the default
        buttons.Controls.Add(_prev);
        buttons.Controls.Add(_cancel);

        // The progress bar sits in the empty half of the button row for the same reason: that row is as tall
        // as the buttons whatever else is in it, so the bar arriving cannot push them down the dialog. It is
        // deliberately given a small size and left to stretch - at its full width it would instead widen the
        // dialog on the way in, since an auto-sized column asks a stretched child how big it currently is.
        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _progress.Size = new Size(Dpi(60), Dpi(6));
        _progress.Margin = new Padding(0, 0, Dpi(16), 0);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, Dpi(12), 0, 0)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_progress, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        root.Controls.Add(bottom, 0, 2);
        root.SetColumnSpan(bottom, 2);

        Controls.Add(root);
        AcceptButton = _next;
        MinimumSize = new Size(Dpi(460), 0);

        // Typing marks the hits already on screen, after a pause so a burst of keystrokes costs one pass.
        _text.TextChanged += (_, _) => { _preview.Stop(); _preview.Start(); };
        _text.DropDown += (_, _) => { if (History is { } h) SetHistory(h()); };
        _regex.CheckedChanged += (_, _) => { _preview.Stop(); _preview.Start(); };
        _case.CheckedChanged += (_, _) => { _preview.Stop(); _preview.Start(); };
        _preview.Tick += (_, _) =>
        {
            _preview.Stop();
            PreviewChanged?.Invoke(_text.Text.Length == 0 ? null : new FindQuery(_text.Text, _regex.Checked, _case.Checked));
        };
    }

    /// <summary>Where the terms searched for before come from. Read when the drop-down opens rather than
    /// pushed after every search: rebuilding the list puts the caret back to the start of the box and drops
    /// any selection, which is not what pressing Enter should do to what you just typed.
    /// A field, not a property - the WinForms analyser flags public properties on a Control.</summary>
    public Func<IEnumerable<string>>? History;

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

    /// <summary>Find keys work anywhere in the dialog, not just in the text box. ProcessCmdKey runs before
    /// the default-button handling that would otherwise swallow them: Shift+Enter used to click "Find Next"
    /// (a form's AcceptButton ignores Alt and Ctrl, but not Shift) and F3 never reached the dialog at all.
    /// Only plain Enter defers to a focused button, so Cancel and Find Previous still activate normally.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter when ActiveControl is not Button:
                Run(true);
                return true;
            case Keys.Shift | Keys.Enter:
            case Keys.Shift | Keys.F3:
                Run(false);
                return true;
            case Keys.F3:
                Run(true);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { HideAndReturnFocus(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideAndReturnFocus(); }
        base.OnFormClosing(e);
    }

    /// <summary>Hides the bar and hands the keyboard back to the window behind it. Without the explicit
    /// activation the hidden dialog stays the application's active form, and keystrokes go nowhere until
    /// something is clicked.</summary>
    private void HideAndReturnFocus()
    {
        var owner = Owner;
        Hide();
        try { owner?.Activate(); } catch { /* the window may be closing */ }
    }

    private void Run(bool forward)
    {
        if (_searching || _text.Text.Length == 0) return;
        _search(new FindQuery(_text.Text, _regex.Checked, _case.Checked), forward);
    }

    public void SetStatus(string text)
    {
        if (_status.Text != text) _status.Text = text;
    }

    /// <summary>Shows/hides the in-progress UI (progress bar + Cancel) and enables/disables the find
    /// buttons while a background search runs. Doing nothing when nothing changed matters: this sits in an
    /// auto-sizing dialog, so each flip is a full relayout, and an answer already gathered needs none.</summary>
    public void SetSearching(bool searching)
    {
        if (_searching == searching) return;
        _searching = searching;
        _next.Enabled = _prev.Enabled = !searching;
        _cancel.Enabled = searching;
        _progress.Visible = searching;
        if (searching) { _progress.Value = 0; SetStatus("Searching\u2026"); }
    }

    /// <summary>Updates the search progress bar (fraction 0..1).</summary>
    public void SetProgress(double fraction)
    {
        if (!_progress.Visible) return;
        int v = (int)Math.Clamp(fraction * 1000, 0, 1000);
        // Windows slides the fill towards a rising value, and a search ends long before the slide arrives -
        // the bar crawled to a seventh full while the search was already at four fifths. Stepping DOWN is
        // immediate, so it is set from just above to snap it to the real figure.
        if (v < _progress.Maximum) { _progress.Value = v + 1; _progress.Value = v; }
        else _progress.Value = v;
    }

    public void FocusInput() { _text.Focus(); _text.SelectionStart = 0; _text.SelectionLength = _text.Text.Length; }

    internal void SetTermForTesting(string text, int start, int length)
    {
        _text.Text = text;
        _text.SelectionStart = start;
        _text.SelectionLength = length;
    }

    internal string TermForTesting() => _text.Text;

    internal (int Start, int Length) SelectionForTesting() => (_text.SelectionStart, _text.SelectionLength);

    internal bool SearchingForTesting() => _searching;

    internal void EnterForTesting() => Run(true);
}

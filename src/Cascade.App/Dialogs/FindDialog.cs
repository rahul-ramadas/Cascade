using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Find;

namespace Cascade.App;

/// <summary>Modeless find bar. Hides (rather than disposing) on Esc or close so it can be reused,
/// and reports status (e.g. still-loading) back via <see cref="SetStatus"/>.</summary>
public sealed class FindDialog : Form
{
    private readonly TextBox _text = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _regex = new() { Text = "&Regex", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox _case = new() { Text = "&Case sensitive", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Gray, Anchor = AnchorStyles.Left };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Continuous, Maximum = 1000, Visible = false };
    private readonly Button _next = new() { Text = "&Find Next" };
    private readonly Button _prev = new() { Text = "Find &Previous" };
    private readonly Button _cancel = new() { Text = "Cancel", Visible = false };
    private readonly Action<FindQuery, bool> _search;
    private bool _searching;

    /// <summary>Raised when the user clicks Cancel while a search is running.</summary>
    public event Action? CancelRequested;

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

        var findLabel = new Label
        {
            Text = "Fi&nd:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, Dpi(7), Dpi(10), Dpi(4))
        };
        _text.Margin = new Padding(0, Dpi(4), 0, Dpi(4));
        _text.MinimumSize = new Size(Dpi(340), 0);
        root.Controls.Add(findLabel);
        root.Controls.Add(_text);

        var options = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0, Dpi(2), 0, Dpi(2))
        };
        _regex.Margin = new Padding(0, Dpi(3), Dpi(24), Dpi(3));
        _case.Margin = new Padding(0, Dpi(3), 0, Dpi(3));
        options.Controls.Add(_regex);
        options.Controls.Add(_case);
        root.Controls.Add(Spacer());
        root.Controls.Add(options);

        foreach (var b in new[] { _next, _prev, _cancel })
        {
            b.AutoSize = true;
            b.MinimumSize = new Size(Dpi(104), Dpi(26));
            b.Margin = new Padding(Dpi(6), 0, 0, 0);
        }
        _next.Click += (_, _) => Run(true);
        _prev.Click += (_, _) => Run(false);
        _cancel.Click += (_, _) => CancelRequested?.Invoke();

        // Right-aligned, like every other dialog's buttons; they used to hang off the left with a wide gap.
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, Dpi(10), 0, 0)
        };
        buttons.Controls.Add(_next);     // rightmost, and the default
        buttons.Controls.Add(_prev);
        buttons.Controls.Add(_cancel);

        _progress.Height = Dpi(6);
        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0, Dpi(8), 0, 0);
        _status.Margin = new Padding(0, Dpi(6), 0, 0);
        _status.Visible = false;
        root.Controls.Add(Spacer());
        root.Controls.Add(_progress);
        root.Controls.Add(Spacer());
        root.Controls.Add(_status);

        root.Controls.Add(buttons);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = _next;
        MinimumSize = new Size(Dpi(460), 0);
    }

    /// <summary>A cell that takes no room, for rows with nothing in the label column.</summary>
    private static Panel Spacer() => new() { Margin = Padding.Empty, Size = Size.Empty, AutoSize = false };

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
        _status.Text = text;
        _status.Visible = text.Length > 0;   // an empty label still stands a text line tall
    }

    /// <summary>Shows/hides the in-progress UI (progress bar + Cancel) and enables/disables the find
    /// buttons while a background search runs.</summary>
    public void SetSearching(bool searching)
    {
        _searching = searching;
        _next.Enabled = _prev.Enabled = !searching;
        _cancel.Visible = _progress.Visible = searching;
        if (searching) { _progress.Value = 0; SetStatus("Searching\u2026"); }
    }

    /// <summary>Updates the search progress bar (fraction 0..1).</summary>
    public void SetProgress(double fraction)
    {
        if (!_progress.Visible) return;
        _progress.Value = (int)Math.Clamp(fraction * 1000, 0, 1000);
    }

    public void FocusInput() { _text.Focus(); _text.SelectAll(); }
}

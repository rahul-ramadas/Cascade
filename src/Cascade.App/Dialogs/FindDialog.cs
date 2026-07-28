using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Find;

namespace Cascade.App;

/// <summary>Modeless find bar. Hides (rather than disposing) on Esc or close so it can be reused,
/// and reports status (e.g. still-loading) back via <see cref="SetStatus"/>.</summary>
public sealed class FindDialog : Form
{
    private readonly TextBox _text = new();
    private readonly CheckBox _regex = new() { Text = "Regex", AutoSize = true, Margin = new Padding(12, 4, 0, 3) };
    private readonly CheckBox _case = new() { Text = "Case", AutoSize = true, Margin = new Padding(12, 4, 0, 3) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Continuous, Maximum = 1000, Visible = false };
    private readonly Button _next = new() { Text = "Find Next" };
    private readonly Button _prev = new() { Text = "Find Previous" };
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
        _text.Width = Dpi(240);
        _text.Font = new Font("Consolas", 9.75f);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(Dpi(12)) };

        var findRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        findRow.Controls.Add(new Label { Text = "Find:", AutoSize = true, Margin = new Padding(0, 6, 6, 3) });
        findRow.Controls.Add(_text);
        findRow.Controls.Add(_regex);
        findRow.Controls.Add(_case);
        root.Controls.Add(findRow);

        _next.AutoSize = true; _next.MinimumSize = new Size(Dpi(92), Dpi(26)); _next.Margin = new Padding(0, Dpi(8), Dpi(6), 0);
        _prev.AutoSize = true; _prev.MinimumSize = new Size(Dpi(104), Dpi(26)); _prev.Margin = new Padding(0, Dpi(8), Dpi(6), 0);
        _cancel.AutoSize = true; _cancel.MinimumSize = new Size(Dpi(80), Dpi(26)); _cancel.Margin = new Padding(0, Dpi(8), 0, 0);
        _next.Click += (_, _) => Run(true);
        _prev.Click += (_, _) => Run(false);
        _cancel.Click += (_, _) => CancelRequested?.Invoke();
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_prev);
        buttons.Controls.Add(_cancel);
        root.Controls.Add(buttons);

        _progress.Width = Dpi(320);
        _progress.Height = Dpi(8);
        _progress.Margin = new Padding(0, Dpi(10), 0, 0);
        root.Controls.Add(_progress);
        root.Controls.Add(_status);

        Controls.Add(root);
        AcceptButton = _next;
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
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    private void Run(bool forward)
    {
        if (_searching || _text.Text.Length == 0) return;
        _search(new FindQuery(_text.Text, _regex.Checked, _case.Checked), forward);
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>Shows/hides the in-progress UI (progress bar + Cancel) and enables/disables the find
    /// buttons while a background search runs.</summary>
    public void SetSearching(bool searching)
    {
        _searching = searching;
        _next.Enabled = _prev.Enabled = !searching;
        _cancel.Visible = _progress.Visible = searching;
        if (searching) { _progress.Value = 0; _status.Text = "Searching\u2026"; }
    }

    /// <summary>Updates the search progress bar (fraction 0..1).</summary>
    public void SetProgress(double fraction)
    {
        if (!_progress.Visible) return;
        _progress.Value = (int)Math.Clamp(fraction * 1000, 0, 1000);
    }

    public void FocusInput() { _text.Focus(); _text.SelectAll(); }
}

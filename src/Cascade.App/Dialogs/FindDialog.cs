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
    private readonly Action<FindQuery, bool> _search;

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

        var next = new Button { Text = "Find Next", AutoSize = true, MinimumSize = new Size(Dpi(92), Dpi(26)), Margin = new Padding(0, Dpi(8), Dpi(6), 0) };
        var prev = new Button { Text = "Find Previous", AutoSize = true, MinimumSize = new Size(Dpi(104), Dpi(26)), Margin = new Padding(0, Dpi(8), 0, 0) };
        next.Click += (_, _) => Run(true);
        prev.Click += (_, _) => Run(false);
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.Add(next);
        buttons.Controls.Add(prev);
        root.Controls.Add(buttons);
        root.Controls.Add(_status);

        Controls.Add(root);
        AcceptButton = next;

        _text.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Run(!e.Shift); e.Handled = e.SuppressKeyPress = true; } };
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
        if (_text.Text.Length == 0) return;
        _search(new FindQuery(_text.Text, _regex.Checked, _case.Checked), forward);
    }

    public void SetStatus(string text) => _status.Text = text;

    public void FocusInput() { _text.Focus(); _text.SelectAll(); }
}

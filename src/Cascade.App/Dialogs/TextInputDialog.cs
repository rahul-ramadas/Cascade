using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>A one-line prompt ("Preset name"), used wherever something just needs a name.</summary>
public sealed class TextInputDialog : DialogBase
{
    private readonly TextBox _text = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };

    public TextInputDialog(string title, string prompt, string initial = "")
    {
        Text = title;
        _text.Text = initial;
        _text.Width = Dpi(280);
        _text.AccessibleName = prompt;

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };
        root.Controls.Add(FieldLabel(prompt), 0, 0);
        root.Controls.Add(_text, 0, 1);
        root.Controls.Add(OkCancelRow(out var ok, out _), 0, 2);
        ok.Enabled = initial.Trim().Length > 0;
        _text.TextChanged += (_, _) => ok.Enabled = _text.Text.Trim().Length > 0;

        Controls.Add(root);
        Shown += (_, _) => { _text.Focus(); _text.SelectAll(); };
    }

    public string Value => _text.Text.Trim();

    /// <summary>Prompts for a name, returning null if the user backs out or clears it.</summary>
    public static string? Ask(IWin32Window owner, string title, string prompt, string initial = "")
    {
        using var dlg = new TextInputDialog(title, prompt, initial);
        return dlg.ShowDialog(owner) == DialogResult.OK && dlg.Value.Length > 0 ? dlg.Value : null;
    }
}

using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

public sealed class GoToDialog : DialogBase
{
    private readonly NumericUpDown _line = new() { Minimum = 1, Maximum = long.MaxValue, ThousandsSeparator = true };

    public long LineNumber => (long)_line.Value;

    public GoToDialog(long maxLine, long current)
    {
        Text = "Go To Line";
        _line.Width = Dpi(180);
        _line.Maximum = Math.Max(1, maxLine);
        _line.Value = Math.Clamp(current, 1, _line.Maximum);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(Dpi(14), Dpi(12), Dpi(14), Dpi(10))
        };
        root.Controls.Add(new Label { Text = $"Line number (1 – {_line.Maximum:N0}):", AutoSize = true, Margin = new Padding(0, 0, 0, Dpi(6)) });
        root.Controls.Add(_line);
        root.Controls.Add(OkCancelRow(out _, out _));

        Controls.Add(root);
        MinimumSize = new Size(Dpi(300), 0);
        _line.Select(0, _line.Text.Length);
    }
}

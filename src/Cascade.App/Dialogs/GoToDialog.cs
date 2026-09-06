using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

public sealed class GoToDialog : DialogBase
{
    private readonly NumericUpDown _line = new() { Minimum = 1, Maximum = long.MaxValue, ThousandsSeparator = true };

    public long LineNumber => (long)_line.Value;

    /// <summary>The lines that can be gone to. A crop is a stretch of the file with the file's own numbering,
    /// so the range offered starts where the crop does rather than at 1 - asking for a line outside it would
    /// mean leaving the view the reader has set up.</summary>
    public GoToDialog(long minLine, long maxLine, long current)
    {
        Text = "Go To Line";
        _line.Width = Dpi(180);
        _line.Minimum = Math.Max(1, minLine);
        _line.Maximum = Math.Max(_line.Minimum, maxLine);
        _line.Value = Math.Clamp(current, _line.Minimum, _line.Maximum);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(Dpi(14), Dpi(12), Dpi(14), Dpi(10))
        };
        root.Controls.Add(new Label { Text = $"Line number ({_line.Minimum:N0} – {_line.Maximum:N0}):", AutoSize = true, Margin = new Padding(0, 0, 0, Dpi(6)) });
        root.Controls.Add(_line);
        root.Controls.Add(OkCancelRow(out _, out _));

        Controls.Add(root);
        MinimumSize = new Size(Dpi(300), 0);
        _line.Select(0, _line.Text.Length);
    }
}

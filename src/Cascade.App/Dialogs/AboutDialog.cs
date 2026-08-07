using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Updating;

namespace Cascade.App;

/// <summary>
/// Identifies the build precisely enough to act on a bug report: version, commit, runtime, and where the
/// executable and settings actually live. Everything is read from the assembly at run time, so it is right
/// by construction rather than by remembering to edit it.
/// </summary>
public sealed class AboutDialog : DialogBase
{
    /// <summary>One line of the box. <paramref name="IsProblem"/> rows are worth noticing, so they are
    /// coloured and may run to several lines rather than being cut off at the edge of the dialog.</summary>
    internal readonly record struct Row(string Label, string Value, bool IsProblem = false);

    public AboutDialog(UpdateService? updater) : this(Describe(updater)) { }

    internal AboutDialog(IReadOnlyList<Row> rows)
    {
        Text = "About Cascade";

        // Size the value column to what it actually has to show - a truncated install path is exactly the
        // detail a bug report needs - but never let one long path stretch the dialog off the screen.
        // Anything longer than that wraps; it is not silently cut.
        int widest = rows.Max(r => TextRenderer.MeasureText(r.Value, Font).Width);
        int valueWidth = Math.Clamp(widest + Dpi(8), Dpi(300), Dpi(620));

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(Dpi(14), Dpi(12), Dpi(14), Dpi(10))
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Cascade",
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size * 1.6f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, Dpi(2))
        };
        root.Controls.Add(title);
        root.SetColumnSpan(title, 2);

        var tagline = new Label
        {
            Text = "A fast, hierarchical-filtering log and text analyzer.",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, Dpi(12))
        };
        root.Controls.Add(tagline);
        root.SetColumnSpan(tagline, 2);

        foreach (var row in rows) AddRow(root, row, valueWidth);

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(Dpi(84), Dpi(26)),
            Margin = new Padding(0, Dpi(12), 0, 0)
        };
        var row2 = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        row2.Controls.Add(close);
        root.Controls.Add(row2);
        root.SetColumnSpan(row2, 2);
        AcceptButton = close;
        CancelButton = close;

        Controls.Add(root);
    }

    /// <summary>What this build has to say about itself. Everything is read at run time, so it is right by
    /// construction rather than by remembering to edit it.</summary>
    private static List<Row> Describe(UpdateService? updater)
    {
        var rows = new List<Row> { new("Version", AppInfo.DisplayVersion) };
        if (AppInfo.Commit is { } commit) rows.Add(new("Commit", commit));
        rows.Add(new("Runtime", $"{AppInfo.Runtime} ({AppInfo.Architecture})"));
        rows.Add(new("Location", AppInfo.ExePath));
        rows.Add(new("Settings", AppSettings.FilePath));
        rows.Add(new("State", MachineState.FilePath));
        rows.Add(new("Updates", UpdateStatusText(updater)));
        return rows;
    }

    /// <summary>Says plainly whether an update is waiting, and why one is not, rather than staying silent
    /// about a check that has been failing for weeks.</summary>
    private static string UpdateStatusText(UpdateService? updater)
    {
        if (updater is null)
            return AppInfo.IsDevBuild ? "Disabled for local builds" : "Disabled";
        if (updater.PendingVersion is { } v)
            return $"Version {v} will be installed when Cascade closes";
        if (updater.LastError is { Length: > 0 } err)
            return "Last check failed: " + err;
        return $"Up to date (checked at startup)";
    }

    /// <summary>
    /// A label/value pair, the two written on the same line as each other.
    ///
    /// The values are text boxes so they can be selected and pasted into a bug report, and a text box draws
    /// its caption at the top of its own box while an auto-sized label centres its in the cell - which put
    /// the two 3-4 pixels apart. Both are therefore pinned to the TOP of the row and left at their own
    /// natural height, so a value long enough to wrap grows the row downwards and its first line still sits
    /// beside the label rather than being cut off at the edge of the dialog.
    /// </summary>
    private void AddRow(TableLayoutPanel root, Row row, int valueWidth)
    {
        int height = TextRenderer.MeasureText(row.Value, Font, new Size(valueWidth, int.MaxValue),
                                              TextFormatFlags.WordBreak).Height;

        root.Controls.Add(new Label
        {
            Text = row.Label,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(0, Dpi(3), Dpi(14), Dpi(3))
        });
        root.Controls.Add(new TextBox
        {
            Text = row.Value,
            ReadOnly = true,
            TabStop = false,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            ForeColor = row.IsProblem ? Color.Firebrick : SystemColors.ControlText,
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.None,
            AutoSize = false,
            Size = new Size(valueWidth, height),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(0, Dpi(3), 0, Dpi(3))
        });
    }

    /// <summary>Test seam: the label and value of every row, in the order they are shown.</summary>
    internal IEnumerable<(Control Label, Control Value)> RowsForTesting
    {
        get
        {
            var table = (TableLayoutPanel)Controls[0];
            for (int i = 0; i < table.Controls.Count - 1; i++)
                if (table.Controls[i] is Label l && table.GetColumnSpan(l) == 1
                    && table.Controls[i + 1] is TextBox v)
                    yield return (l, v);
        }
    }
}

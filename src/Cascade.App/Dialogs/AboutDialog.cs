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
    public AboutDialog(UpdateService? updater)
    {
        Text = "About Cascade";

        var rows = new List<(string Label, string Value)> { ("Version", AppInfo.DisplayVersion) };
        if (AppInfo.Commit is { } commit) rows.Add(("Commit", commit));
        rows.Add(("Runtime", $"{AppInfo.Runtime} ({AppInfo.Architecture})"));
        rows.Add(("Location", AppInfo.ExePath));
        rows.Add(("Settings", AppSettings.FilePath));
        rows.Add(("Updates", UpdateStatusText(updater)));

        // Size the value column to what it actually has to show - a truncated install path is exactly the
        // detail a bug report needs - but never let one long path stretch the dialog off the screen.
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

        foreach (var (label, value) in rows) AddRow(root, label, value, valueWidth);

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(Dpi(84), Dpi(26)),
            Margin = new Padding(0, Dpi(12), 0, 0)
        };
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        row.Controls.Add(close);
        root.Controls.Add(row);
        root.SetColumnSpan(row, 2);
        AcceptButton = close;
        CancelButton = close;

        Controls.Add(root);
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

    /// <summary>A label/value pair. Values are selectable so they can be pasted into a bug report, but are
    /// kept out of the tab order so the dialog does not open with one of them highlighted.</summary>
    private void AddRow(TableLayoutPanel root, string label, string value, int valueWidth)
    {
        root.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, Dpi(3), Dpi(14), Dpi(3))
        });
        root.Controls.Add(new TextBox
        {
            Text = value,
            ReadOnly = true,
            TabStop = false,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            AutoSize = false,
            Width = valueWidth,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, Dpi(3), 0, Dpi(3))
        });
    }
}

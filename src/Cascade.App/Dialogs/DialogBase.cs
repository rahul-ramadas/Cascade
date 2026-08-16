using System.Windows.Forms;

namespace Cascade.App;

/// <summary>
/// Base for all modal dialogs. Auto-sizes to its content (DPI-proof: no fixed client size to clip),
/// centers on the parent, and dismisses on Esc. Derived dialogs build a single root
/// <see cref="TableLayoutPanel"/> whose last row is the button strip, avoiding Dock overlap.
/// </summary>
public abstract class DialogBase : Form
{
    protected DialogBase()
    {
        Automation.Suppress(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;   // a fixed dialog shows none anyway; this keeps the resizable ones matching
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
    }

    /// <summary>Scales a logical (96-DPI) pixel value to the current DPI.</summary>
    protected int Dpi(int logical) => LogicalToDeviceUnits(logical);

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    /// <summary>Right-aligned OK/Cancel strip (OK left of Cancel). The row flows right to left, so Cancel is
    /// added first to land on the right - which would also make it the first thing Tab reaches. The two are
    /// given their tab order outright, so the keyboard walks them the way they are read.</summary>
    protected FlowLayoutPanel OkCancelRow(out Button ok, out Button cancel)
    {
        ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(Dpi(84), Dpi(26)), Margin = new Padding(Dpi(6), 0, 0, 0), TabIndex = 0 };
        cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Dpi(84), Dpi(26)), Margin = new Padding(Dpi(6), 0, 0, 0), TabIndex = 1 };
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,        // the pair belongs on one line, however narrow the space offered
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, Dpi(10), 0, 0)
        };
        row.Controls.Add(cancel); // rightmost
        row.Controls.Add(ok);     // left of cancel
        AcceptButton = ok;
        CancelButton = cancel;
        return row;
    }

    protected static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 6, 8, 3),
        TextAlign = ContentAlignment.MiddleLeft
    };
}

using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>
/// A menu item that is a command in its own right and also has a submenu under it.
///
/// <para>WinForms stops drawing an item's shortcut the moment it is given children - reasonably, since a
/// parent is usually only a way in to what is underneath. This one is not: clicking it is what turns field
/// splitting on and off, and the items beneath only choose how the fields are laid out. Dropping the key
/// from the line would leave the one command a reader is most likely to want a key for as the only one in
/// the menu not saying what its key is.</para>
///
/// <para>So the shortcut is drawn back on, right-aligned in the item's own content the way the framework
/// aligns every other one, with the arrow's width left clear at the end.</para>
/// </summary>
internal sealed class CommandWithSubmenu : ToolStripMenuItem
{
    public CommandWithSubmenu(string text, EventHandler onClick) : base(text, null, onClick) { }

    /// <summary>Answers the item's own shortcut. WinForms refuses to, for the same reason it stops drawing
    /// it - an item with children is taken to be a way in rather than a command - so without this the key
    /// went nowhere the moment the item was given something to nest under it.</summary>
    protected override bool ProcessCmdKey(ref Message m, Keys keyData)
    {
        if (Enabled && ShortcutKeys != Keys.None && ShortcutKeys == keyData) { PerformClick(); return true; }
        return base.ProcessCmdKey(ref m, keyData);
    }

    /// <summary>What the framework keeps clear at the right-hand end of a dropdown item for the arrow, plus
    /// the gap it leaves in front of it. Not a figure WinForms will tell you - the layout that knows it is
    /// internal - so it is measured: open the menu, and the shortcut here has to end in line with the ones
    /// the framework draws for the items around it.</summary>
    private int ArrowRoom => 26 * (Owner?.DeviceDpi ?? 96) / 96;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (e is null || !IsOnDropDown || Owner is null) return;

        string? keys = ShortcutKeyDisplayString;
        if (string.IsNullOrEmpty(keys) && ShortcutKeys != Keys.None)
            keys = TypeDescriptor.GetConverter(typeof(Keys)).ConvertToString(ShortcutKeys);
        if (string.IsNullOrEmpty(keys)) return;

        var box = ContentRectangle;
        box.Width -= ArrowRoom;
        if (box.Width <= 0) return;

        // Handed to the renderer exactly as the framework hands it the item's own label, rather than drawn
        // straight onto the Graphics: the colour worked out here is only a proposal, and a renderer is free
        // to overrule it. The one in use does - it highlights a menu row in pale blue and leaves the text
        // dark - so a shortcut painted directly in HighlightText came out white on pale blue.
        Color ink = !Enabled ? SystemColors.GrayText
                  : Selected || Pressed ? SystemColors.HighlightText
                  : SystemColors.MenuText;
        Owner.Renderer.DrawItemText(
            new ToolStripItemTextRenderEventArgs(e.Graphics, this, keys, box, ink, Font, ContentAlignment.MiddleRight));
    }
}

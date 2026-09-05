using System.Windows.Forms;

namespace Cascade.App;

/// <summary>A radio button whose Alt key picks it without taking the keyboard, the same manners
/// <see cref="QuietCheckBox"/> has and for the same reason: these sit in a dialog whose point is the text box
/// above them, and changing your mind about an option must not throw away the caret and the selection in a
/// pattern that is half typed.</summary>
internal sealed class QuietRadioButton : RadioButton
{
    protected override bool ProcessMnemonic(char charCode)
    {
        if (!UseMnemonic || !CanSelect || !IsMnemonic(charCode, Text)) return false;
        Checked = true;
        return true;
    }
}

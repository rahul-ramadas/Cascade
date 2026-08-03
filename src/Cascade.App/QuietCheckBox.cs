using System.Windows.Forms;

namespace Cascade.App;

/// <summary>A check box whose Alt key ticks it without taking the keyboard. The stock behaviour selects the
/// box it ticks, which throws away the caret and any selection in whatever the user was typing in - and
/// these boxes sit beside text boxes precisely so they can be changed mid-term.</summary>
internal sealed class QuietCheckBox : CheckBox
{
    protected override bool ProcessMnemonic(char charCode)
    {
        if (!UseMnemonic || !CanSelect || !IsMnemonic(charCode, Text)) return false;
        // The same order a click walks, so the key and the mouse agree.
        CheckState = ThreeState
            ? CheckState switch
            {
                CheckState.Unchecked => CheckState.Checked,
                CheckState.Checked => CheckState.Indeterminate,
                _ => CheckState.Unchecked
            }
            : Checked ? CheckState.Unchecked : CheckState.Checked;
        return true;
    }
}

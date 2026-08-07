using System.Windows.Forms;

namespace Cascade.App;

/// <summary>A check box whose Alt key ticks it without taking the keyboard, and whose three-state cycle can
/// be turned round.
///
/// The stock Alt behaviour selects the box it ticks, which throws away the caret and any selection in
/// whatever the user was typing in - and these boxes sit beside text boxes precisely so they can be changed
/// mid-term.
///
/// <see cref="TurnsOnFirst"/> exists because Windows cycles a three-state box unchecked - checked -
/// indeterminate, which is the wrong way round for a box that rests on the third state: from "inherited",
/// the first press gives "off" when what was asked for is plainly "on". The cycle is driven here rather than
/// left to <see cref="CheckBox.AutoCheck"/> so that the state settles before Click is raised - letting the
/// stock cycle run and correcting it afterwards would report the intermediate state to every handler.</summary>
internal sealed class QuietCheckBox : CheckBox
{
    /// <summary>Cycle set, cleared, then don't-care - rather than the stock cleared, set, don't-care. A
    /// field, not a property: the WinForms analyzer refuses public properties on a Control.</summary>
    internal bool TurnsOnFirst;

    public QuietCheckBox() => AutoCheck = false;

    protected override void OnClick(EventArgs e)
    {
        if (AutoCheck) { base.OnClick(e); return; }   // whoever put it back wants the stock behaviour
        CheckState = Next(CheckState);
        base.OnClick(e);
    }

    protected override bool ProcessMnemonic(char charCode)
    {
        if (!UseMnemonic || !CanSelect || !IsMnemonic(charCode, Text)) return false;
        // The same order a click walks, so the key and the mouse agree.
        CheckState = Next(CheckState);
        return true;
    }

    /// <summary>Test seam: what a click, the space bar and a screen reader's default action all come down
    /// to.</summary>
    internal void PressForTesting() => OnClick(EventArgs.Empty);

    private CheckState Next(CheckState from)
    {
        if (!ThreeState) return from == CheckState.Checked ? CheckState.Unchecked : CheckState.Checked;

        return TurnsOnFirst
            ? from switch
            {
                CheckState.Indeterminate => CheckState.Checked,
                CheckState.Checked => CheckState.Unchecked,
                _ => CheckState.Indeterminate
            }
            : from switch
            {
                CheckState.Unchecked => CheckState.Checked,
                CheckState.Checked => CheckState.Indeterminate,
                _ => CheckState.Unchecked
            };
    }
}

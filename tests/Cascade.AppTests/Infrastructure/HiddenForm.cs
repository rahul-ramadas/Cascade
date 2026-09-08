namespace Cascade.AppTests;

/// <summary>
/// The window a check hosts a control in. A plain <see cref="Form"/> in every respect except that showing
/// it does not take the desktop's foreground, which is what stopped a run being usable alongside anything
/// else on the machine.
///
/// <para><c>ShowWithoutActivation</c> is the framework's own way to say this and the only one it honours -
/// a CBT hook setting WS_EX_NOACTIVATE was measured to change nothing, because WinForms writes its own
/// extended styles from CreateParams and shows with SW_SHOW regardless.</para>
///
/// <para>Not activating leaves the window the active one of nobody, so nothing on it has the keyboard
/// until something asks: a check that presses a key only the focused control handles has to say
/// <c>Focus()</c> first. That is the thread-local way to put it back. <c>SetActiveWindow</c> looks like the
/// same thing and is not - on a process already holding the foreground it moves the desktop too, and was
/// measured to give back half of what this saves.</para>
/// </summary>
internal sealed class HiddenForm : Form
{
    protected override bool ShowWithoutActivation => true;
}

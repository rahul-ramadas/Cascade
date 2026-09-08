namespace Cascade.AppTests;

/// <summary>
/// Putting a window up without putting it on the developer's desktop.
///
/// <para>Several things a window does only work once it has one: <c>Button.PerformClick</c> refuses on a
/// control that cannot be selected, and nothing can be selected inside a form that has never been shown.
/// Opacity alone is not enough either - a form shown at zero opacity still takes the foreground, which
/// means a run of these tests steals the keyboard from whatever the reader was typing into.</para>
///
/// <para>So the window goes beyond the last monitor as well, which is the same trick
/// <c>CASCADE_TEST_OFFSCREEN</c> plays on the real app for the UI suite, and out of the taskbar.</para>
/// </summary>
internal static class Hidden
{
    public static T Show<T>(T form) where T : Form
    {
        form.Opacity = 0;
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        var desktop = SystemInformation.VirtualScreen;
        form.Location = new Point(desktop.Right + 200, desktop.Top);
        form.Show();
        return form;
    }
}

using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>
/// A single very short tint over the whole window, used to say "there is nothing further that way".
/// <para>
/// It is a click-through, non-activating layered form owned by the main window, so it never takes focus,
/// never shows in the taskbar, and is gone again in about a tenth of a second - long enough to catch the
/// eye, short enough not to sit in the way. The wording of what happened goes to the status bar instead.
/// </para>
/// </summary>
internal sealed class AppFlash : Form
{
    private const int StepMs = 24;
    private static readonly double[] Steps = { 0.24, 0.17, 0.10, 0.04 };
    private static readonly Color Tint = Color.FromArgb(202, 58, 52);

    /// <summary>Only ever one on screen: holding a find key must not stack flashes.</summary>
    private static AppFlash? _current;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = StepMs };
    private int _step;

    public static void Flash(Form? owner)
    {
        if (owner is null || owner.IsDisposed || !owner.IsHandleCreated) return;
        if (owner.WindowState == FormWindowState.Minimized) return;

        _current?.Finish();
        var flash = new AppFlash(owner);
        _current = flash;
        flash.Show(owner);
        flash._timer.Start();
    }

    /// <summary>Removes any flash still on screen (used when the window is closing).</summary>
    public static void Clear() => _current?.Finish();

    private AppFlash(Form owner)
    {
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Tint;
        Opacity = Steps[0];
        // The client area, so the tint covers the app itself rather than its title bar and border.
        Bounds = owner.RectangleToScreen(owner.ClientRectangle);

        _timer.Tick += (_, _) =>
        {
            if (++_step >= Steps.Length) { Finish(); return; }
            Opacity = Steps[_step];
        };
    }

    /// <summary>Never take focus when shown.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 // WS_EX_NOACTIVATE: clicking it can never activate it
                        | 0x00000020; // WS_EX_TRANSPARENT: the mouse passes straight through
            return cp;
        }
    }

    private void Finish()
    {
        if (_current == this) _current = null;
        _timer.Stop();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

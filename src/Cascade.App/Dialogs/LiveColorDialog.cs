using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>The system colour picker, reporting the colour as it is being chosen rather than only when OK
/// is pressed, so a preview elsewhere can follow along.
///
/// The common dialog has no "colour changed" notification of any kind - the hook procedure hears about
/// initialisation and about OK, and nothing in between. So this watches the Red, Green and Blue edit boxes
/// on a timer. Their identifiers are the ones in the SDK's own Color.dlg template, which is what the
/// dialog is built from. If a future Windows ever renumbers them, GetDlgItemInt says so and the preview
/// simply stops following; nothing else depends on it.</summary>
internal sealed class LiveColorDialog : ColorDialog
{
    private const int WM_INITDIALOG = 0x0110, WM_TIMER = 0x0113, WM_DESTROY = 0x0002;
    private const int ColorRed = 706, ColorGreen = 707, ColorBlue = 708;   // dlgs.h
    private static readonly IntPtr TimerId = 0xC0107;

    private Color _reported;

    /// <summary>Raised while the dialog is open, every time the colour under the cursor changes.</summary>
    internal event Action<Color>? Previewing;

    internal LiveColorDialog(Color start)
    {
        FullOpen = true;         // the RGB boxes only exist on the full version
        Color = start;
        _reported = start;
    }

    protected override IntPtr HookProc(IntPtr hWnd, int msg, IntPtr wparam, IntPtr lparam)
    {
        switch (msg)
        {
            case WM_INITDIALOG:
                // Fast enough to feel live while dragging round the spectrum, slow enough to cost nothing.
                SetTimer(hWnd, TimerId, 100, IntPtr.Zero);
                break;
            case WM_TIMER when wparam == TimerId:
                Report(hWnd);
                break;
            case WM_DESTROY:
                KillTimer(hWnd, TimerId);
                break;
        }
        return base.HookProc(hWnd, msg, wparam, lparam);
    }

    private void Report(IntPtr dialog)
    {
        if (Previewing is null) return;
        if (!TryRead(dialog, ColorRed, out int r) ||
            !TryRead(dialog, ColorGreen, out int g) ||
            !TryRead(dialog, ColorBlue, out int b)) return;

        var now = Color.FromArgb(r, g, b);
        if (now == _reported) return;
        _reported = now;
        Previewing(now);
    }

    private static bool TryRead(IntPtr dialog, int control, out int value)
    {
        value = (int)GetDlgItemInt(dialog, control, out bool ok, bSigned: false);
        return ok && value is >= 0 and <= 255;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapseMs, IntPtr callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr id);

    [DllImport("user32.dll")]
    private static extern uint GetDlgItemInt(IntPtr hDlg, int control,
                                             [MarshalAs(UnmanagedType.Bool)] out bool translated,
                                             [MarshalAs(UnmanagedType.Bool)] bool bSigned);
}

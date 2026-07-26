using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>
/// A <see cref="TextBox"/> whose placeholder is the native Win32 <b>cue banner</b> (EM_SETCUEBANNER) —
/// the same grayed hint Explorer's search box uses. WinForms' own <see cref="TextBox.PlaceholderText"/>
/// is a managed re-implementation drawn in WM_PAINT, which visibly flickers when the mouse moves over the
/// box (each hover repaint erases then redraws the hint). The OS cue banner is painted by the edit control
/// itself, so it never flickers.
/// </summary>
internal sealed class CueTextBox : TextBox
{
    private const int EM_SETCUEBANNER = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    private string _cue = "";

    /// <summary>Placeholder text shown (kept visible while focused, until the user types) when empty.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Cue
    {
        get => _cue;
        set { _cue = value ?? ""; if (IsHandleCreated) Apply(); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Apply();
    }

    // wParam = 1: keep the cue visible even when the box has focus (it clears once text is entered).
    private void Apply() => SendMessage(Handle, EM_SETCUEBANNER, (IntPtr)1, _cue);
}

using System.Runtime.InteropServices;

namespace Cascade.AppTests;

/// <summary>
/// Hands the desktop foreground back when a check has taken it.
///
/// <para>The windows these checks build say <c>ShowWithoutActivation</c>, so most of them never reach the
/// desktop at all. Three kinds still do, and cannot be stopped: the app's own main window opens MAXIMISED,
/// and WinForms reads WindowState before it reads that property, so it is shown with SW_SHOWMAXIMIZED and
/// activates; a modal <c>ShowDialog</c> ignores the property by design; and a common dialog like the
/// colour picker is not ours to configure.</para>
///
/// <para>Each of those is a single moment - but the foreground would then STAY in the test host for the
/// rest of the run, because closing the active window promotes another window of the same process rather
/// than giving the desktop back. <c>Checks.Pump</c> calls this, so a check gets it back within
/// milliseconds of taking it. MEASURED with scripts/Measure-Focus.ps1: 26% of a run without it, 0.2%
/// with.</para>
///
/// <para>The window it hands back to is the one immediately behind ours in the z-order, which is the one
/// that lost the foreground when we took it. Remembering a window from the start of the run would be
/// wrong: by then you may well have moved on to something else, and putting the keyboard back where it was
/// half an hour ago is its own interruption.</para>
/// </summary>
internal static class Foreground
{
    private const uint GwHwndNext = 2;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr window, out int process);

    /// <summary>Whether the desktop is pointing at this process - that is, whether a key pressed now would
    /// be eaten by a check instead of reaching whatever the developer meant it for.</summary>
    internal static bool HeldByUs => IsOurs(GetForegroundWindow());

    /// <summary>The window the foreground would go to if we let go of it: the first one at or behind the
    /// foreground that someone else owns, is visible, and has a title - which is a window you were using.
    /// Zero when there is no such window, which is what a machine with nobody sitting at it looks like.
    /// </summary>
    internal static IntPtr Elsewhere()
    {
        for (IntPtr w = GetForegroundWindow(); w != IntPtr.Zero; w = GetWindow(w, GwHwndNext))
        {
            if (!IsOurs(w) && IsWindowVisible(w) && GetWindowTextLength(w) > 0) return w;
        }
        return IntPtr.Zero;
    }

    /// <summary>Gives the foreground back to whoever is behind us, if we are the ones holding it.</summary>
    internal static void Release()
    {
        if (!HeldByUs) return;
        IntPtr next = Elsewhere();
        if (next != IntPtr.Zero) SetForegroundWindow(next);
    }

    private static bool IsOurs(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(window, out int process);
        return process == Environment.ProcessId;
    }
}

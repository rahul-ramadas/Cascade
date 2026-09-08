namespace Cascade.App;

/// <summary>
/// Whether a window this application shows is allowed to take the desktop's foreground.
///
/// <para>It always is, in the product. This exists for the in-process checks, which build about a hundred
/// windows in a run - a real MainForm, every dialog, and a host for each control under test. All of them
/// are already invisible and parked beyond the last monitor, but showing a window ACTIVATES it, so a run
/// on the developer's own machine quietly took the keyboard away from whatever they were typing into.
/// MEASURED with scripts/Measure-Focus.ps1 at 86% of a run, and 26% with this honoured.</para>
///
/// <para>It does not cover everything, and cannot: WinForms reads WindowState before it reads
/// <c>ShowWithoutActivation</c>, so a maximised window - which is how the app opens - activates anyway,
/// and a modal <c>ShowDialog</c> ignores the property by design. The checks put the desktop back for
/// themselves after those; see <c>Cascade.AppTests.Foreground</c>.</para>
///
/// <para>Only the test assembly can set it - the field is internal, and nothing in the product writes to
/// it - so a released build has no way to reach this state.</para>
/// </summary>
internal static class WindowActivation
{
    /// <summary>Show windows without activating them. Honoured by <see cref="DialogBase"/> and
    /// <see cref="MainForm"/>, which are the only two top-level windows the application has.</summary>
    internal static bool Suppressed { get; set; }
}

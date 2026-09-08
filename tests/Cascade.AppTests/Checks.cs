using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text;
using Cascade.App;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;
using Cascade.Core.Persistence;

namespace Cascade.AppTests;

/// <summary>
/// Every check the WinForms half of Cascade is held to: real controls, real dialogs and a real MainForm,
/// built on the STA thread <see cref="Sta"/> owns and never shown on screen.
///
/// <para>A group returns whether all of its checks passed, having reported each one, rather than stopping
/// at the first failure. That is deliberate: one run then names everything a change broke, which is what
/// makes a shared piece of layout cheap to fix. <see cref="AppCheckFixture.Verify"/> turns a group into an
/// xUnit result, and the [Fact]s that call them are grouped by subject in LogViewTests, FilterListTests,
/// FindAndWindowTests and their neighbours.</para>
///
/// <para>The groups themselves live in <c>Checks.*.cs</c> beside this file, one part per subject, matching
/// those test classes. What is here is only the plumbing they share; <c>Checks.Support.cs</c> holds the
/// helpers - pixel reading, pumping the message queue, waiting for a filter pass - that more than one
/// subject needs.</para>
/// </summary>
internal static partial class Checks
{
    internal const string FailMarker = "[FAIL] ";

    /// <summary>Where <see cref="Line"/> writes. Set for the duration of one group by
    /// <see cref="AppCheckFixture.Verify"/>, on the same thread the group runs on, so there is no sharing
    /// to get wrong - the checks are serial by construction.</summary>
    private static List<string>? _captured;

    internal static void CaptureInto(List<string>? lines) => _captured = lines;
}
